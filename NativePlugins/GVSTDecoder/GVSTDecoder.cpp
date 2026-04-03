// GVSTDecoder.cpp  –  Windows MFT H.264 → RGBA32 decoder plugin for Unity.
//
// Architecture:
//   Input  : Raw H.264 Annex-B NAL bytes (start-code prefixed), fed via GVST_Feed().
//   Decode : Microsoft H264 Video Decoder MFT (CLSID_CMSH264DecoderMFT).
//            The MFT automatically selects DXVA2/D3D11VA (NVIDIA, AMD, Intel)
//            or software fallback — no GPU vendor dependency.
//   Output : NV12 (GPU path) or ARGB32 (software) → converted to RGBA32 in-process.
//   Pixels : Delivered to Unity via GVST_GetFrame() → LoadRawTextureData.

// Must be defined before including GVSTDecoder.h so GVST_API expands to dllexport.
// MSBuild also passes /DGVSTDECODER_EXPORTS on the command line; the pragma suppresses
// the resulting C4005 redefinition warning.
#pragma warning(push)
#pragma warning(disable: 4005)
#define GVSTDECODER_EXPORTS
#pragma warning(pop)

#define WIN32_LEAN_AND_MEAN

#include "GVSTDecoder.h"

#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <mferror.h>
#include <mfobjects.h>

#include <vector>
#include <deque>
#include <string>
#include <unordered_map>
#include <mutex>
#include <memory>
#include <atomic>
#include <algorithm>
#include <cstring>

#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "mf.lib")
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")

#include <d3d11.h>
#include <d3d11_1.h>
#include <dxgi.h>
#pragma comment(lib, "d3dcompiler.lib")
#include <d3dcompiler.h>

// Unity Native Plugin API — lets us receive Unity's own ID3D11Device* via UnityPluginLoad.
// Headers copied from Editor/Data/PluginAPI/
#include "IUnityInterface.h"
#include "IUnityGraphicsD3D11.h"

// ── Helpers ───────────────────────────────────────────────────────────────────

template<class T>
static void SafeRelease(T** pp) { if (*pp) { (*pp)->Release(); *pp = nullptr; } }

static inline int Clamp255(int v) { return v < 0 ? 0 : v > 255 ? 255 : v; }

// NV12 → RGBA32 (in-place into a pre-allocated RGBA buffer)
// NV12: Y plane (stride_y × h), then interleaved UV plane (stride_uv × h/2)
static void NV12ToRGBA(const uint8_t* y_plane, int stride_y,
                       const uint8_t* uv_plane, int stride_uv,
                       uint8_t* rgba, int width, int height)
{
    for (int row = 0; row < height; ++row)
    {
        const uint8_t* yRow  = y_plane  + row         * stride_y;
        const uint8_t* uvRow = uv_plane + (row >> 1)  * stride_uv;
        uint8_t*       dst   = rgba     + row          * width * 4;

        for (int col = 0; col < width; col += 2)
        {
            // U/V are shared across a 2×2 block
            int U = (int)uvRow[col    ] - 128;
            int V = (int)uvRow[col + 1] - 128;

            // BT.601 limited-range coefficients (×1024 fixed-point)
            int rdiff =  1634 * V;
            int gdiff = -401  * U - 832 * V;
            int bdiff =  2065 * U;

            for (int x = 0; x < 2 && (col + x) < width; ++x)
            {
                int Y = ((int)yRow[col + x] - 16) * 1192;
                uint8_t* p = dst + (col + x) * 4;
                p[0] = (uint8_t)Clamp255((Y + rdiff) >> 10);  // R
                p[1] = (uint8_t)Clamp255((Y + gdiff) >> 10);  // G
                p[2] = (uint8_t)Clamp255((Y + bdiff) >> 10);  // B
                p[3] = 255;                                     // A
            }
        }
    }
}

// ARGB32 (stored as BGRA in memory) → RGBA32
static void BGRAToRGBA(const uint8_t* bgra, uint8_t* rgba, int width, int height, int stride)
{
    for (int row = 0; row < height; ++row)
    {
        const uint8_t* src = bgra + row * stride;
        uint8_t*       dst = rgba + row * width * 4;
        for (int col = 0; col < width; ++col, src += 4, dst += 4)
        {
            dst[0] = src[2]; // R
            dst[1] = src[1]; // G
            dst[2] = src[0]; // B
            dst[3] = 255;    // A
        }
    }
}

// ── NV12→RGBA compute shader ──────────────────────────────────────────────────
// Compiled at first use from HLSL so we don't need fxc.exe shipped.
// Each thread processes 1 pixel: reads Y[x,y] + UV[x/2, y/2] → RGBA.
// BT.601 limited-range conversion matches the CPU NV12ToRGBA coefficients above.

static const char* s_nv12CsHlsl = R"HLSL(
Texture2D<float>    tY   : register(t0);   // Y  plane  R8_UNORM   (full res)
Texture2D<float2>   tUV  : register(t1);   // UV plane  R8G8_UNORM (half res)
RWTexture2D<float4> tOut : register(u0);   // RGBA32 output

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint w, h;
    tOut.GetDimensions(w, h);
    if (id.x >= w || id.y >= h) return;

    float  Y  = tY [id.xy];
    float2 UV = tUV[id.xy / 2];
    float  U  = UV.x - 0.5f;
    float  V  = UV.y - 0.5f;

    // BT.601 limited range
    float yy = 1.164f * (Y - 0.0627f);
    float r  = saturate(yy + 1.596f * V);
    float g  = saturate(yy - 0.392f * U - 0.813f * V);
    float b  = saturate(yy + 2.017f * U);
    tOut[id.xy] = float4(r, g, b, 1.0f);
}
)HLSL";

static ID3D11ComputeShader* s_nv12Cs   = nullptr;
static std::mutex           s_csMutex;

static bool EnsureNV12ComputeShader(ID3D11Device* dev)
{
    std::lock_guard<std::mutex> lk(s_csMutex);
    if (s_nv12Cs) return true;

    ID3DBlob* code = nullptr;
    ID3DBlob* errs = nullptr;
    HRESULT hr = D3DCompile(
        s_nv12CsHlsl, strlen(s_nv12CsHlsl),
        "NV12toRGBA", nullptr, nullptr,
        "CSMain", "cs_5_0",
        D3DCOMPILE_OPTIMIZATION_LEVEL3, 0,
        &code, &errs);
    if (errs) errs->Release();
    if (FAILED(hr) || !code) return false;

    hr = dev->CreateComputeShader(code->GetBufferPointer(), code->GetBufferSize(),
                                   nullptr, &s_nv12Cs);
    code->Release();
    return SUCCEEDED(hr);
}

// ── Decoder state ──────────────────────────────────────────────────────────────

enum class OutFmt { Unknown, NV12, BGRA };

struct Decoder
{
    IMFTransform*         mft         = nullptr;
    std::atomic<bool>     aborting    { false };    // set by DeleteDecoder before acquiring decoderLock
    int   width    = 0;
    int   height   = 0;
    int   strideY  = 0;
    bool  ready    = false;
    bool  mft_usesD3D = false;  // true when hardware DXVA decode is active
    OutFmt outFmt  = OutFmt::Unknown;
    LONGLONG pts   = 0;

    std::vector<uint8_t> rgba;
    struct ReadyFrame
    {
        int width = 0;
        int height = 0;
        std::vector<uint8_t> rgba;
    };
    std::deque<ReadyFrame> readyFrames;
    static constexpr size_t MaxReadyFrames = 16;
    std::string lastError;

    // Diagnostic counters (read via GVST_GetStats)
    std::atomic<int> stat_processInputOK  {0};
    std::atomic<int> stat_drainProduced   {0};
    std::atomic<int> stat_drainNeedMore   {0};
    std::atomic<int> stat_drainStreamChg  {0};
    std::atomic<int> stat_drainFailed     {0};
    std::atomic<int> stat_lock2dOK        {0};
    std::atomic<int> stat_lock2dFail      {0};
    std::atomic<int> stat_lock1dOK        {0};
    std::atomic<int> stat_mapTimeout      {0};
    std::atomic<int> stat_getFrameOK      {0};
    std::atomic<int> stat_getFrameBufSmall{0};
    std::atomic<int> stat_getFrameEmpty   {0};

    // ── GPU zero-copy path ────────────────────────────────────────────────────
    // DrainOutputs (worker thread) AddRef's the raw DXVA NV12 surface and stores
    // it in pendingNV12.  OnRenderEvent (render thread) grabs it, runs a compute
    // shader (Y+UV planes → RGBA32) into outputTex, then releases the surface.
    // No staging texture. No Map(). No CPU pixel buffer. No memcpy.
    ID3D11Texture2D*           pendingNV12   = nullptr;  // DXVA surface (AddRef'd by worker)
    UINT                       pendingSubIdx = 0;
    std::atomic<bool>          nv12Dirty     {false};    // true = pendingNV12 not yet consumed
    std::mutex                 nv12Mutex;                // guards pendingNV12 hand-off

    ID3D11Texture2D*           outputTex  = nullptr;    // RGBA32 (BIND_SHADER_RESOURCE|UAV)
    ID3D11UnorderedAccessView* uav        = nullptr;    // UAV on outputTex
    ID3D11ShaderResourceView*  srvY       = nullptr;    // SRV: Y  plane R8_UNORM
    ID3D11ShaderResourceView*  srvUV      = nullptr;    // SRV: UV plane R8G8_UNORM
    int                        outputTexW = 0;
    int                        outputTexH = 0;

    // Published pointer for cross-thread reads (C# main thread reads via GVST_GetTexturePtr,
    // render thread writes after creating/resizing outputTex).
    std::atomic<ID3D11Texture2D*> publishedOutputTex{nullptr};

    std::mutex decoderLock;

    // GPU resources are released here.  The destructor runs when the last shared_ptr
    // drops — which is always AFTER any OnRenderEvent snapshot that held a ref has
    // finished iterating (OnRenderEvent checks aborting before touching GPU resources).
    // D3D11 Release() is safe from any thread (it's reference counting only).
    ~Decoder()
    {
        publishedOutputTex.store(nullptr);
        SafeRelease(&srvY);
        SafeRelease(&srvUV);
        SafeRelease(&uav);
        SafeRelease(&outputTex);
        SafeRelease(&pendingNV12);
    }
};

static std::unordered_map<int, std::shared_ptr<Decoder>> s_decoders;
static std::mutex   s_mutex;  // guards s_decoders map only — NOT held during MFT/D3D work
static std::atomic<int> s_nextId{1};

static bool s_mfStarted = false;

// ── D3D11 / DXGI device ────────────────────────────────────────────────────────
// Primary: Unity's own ID3D11Device* received in UnityPluginLoad.
//          MFT decodes on this device → NV12 surfaces already live on Unity's GPU.
// Fallback: standalone device created only when running outside of Unity.
static ID3D11Device*         s_unityDevice    = nullptr;   // never AddRef/Released by us
static ID3D11Device*         s_fallbackDevice = nullptr;   // only used outside Unity
static IMFDXGIDeviceManager* s_dxgiMgr        = nullptr;
static UINT                  s_dxgiToken      = 0;
static std::mutex            s_dxgiMutex;                  // guards creation once

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API
UnityPluginLoad(IUnityInterfaces* unityInterfaces)
{
    IUnityGraphicsD3D11* d3d = unityInterfaces->Get<IUnityGraphicsD3D11>();
    if (!d3d) return;
    s_unityDevice = d3d->GetDevice();

    // Build the DXGI manager around Unity's device immediately so that the first
    // GVST_Initialize call can find it without creating a standalone device.
    std::lock_guard<std::mutex> lk(s_dxgiMutex);
    if (!s_dxgiMgr && s_unityDevice)
    {
        MFCreateDXGIDeviceManager(&s_dxgiToken, &s_dxgiMgr);
        if (s_dxgiMgr) s_dxgiMgr->ResetDevice(s_unityDevice, s_dxgiToken);
    }
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API
UnityPluginUnload()
{
    // Device is owned by Unity — do NOT Release() it.
    s_unityDevice = nullptr;
}

static bool EnsureDXGIManager()
{
    {
        std::lock_guard<std::mutex> lk(s_dxgiMutex);
        if (s_dxgiMgr) return true;

        // If Unity device is already available, use it (avoids extra device alloc).
        if (s_unityDevice)
        {
            MFCreateDXGIDeviceManager(&s_dxgiToken, &s_dxgiMgr);
            if (s_dxgiMgr) s_dxgiMgr->ResetDevice(s_unityDevice, s_dxgiToken);
            return s_dxgiMgr != nullptr;
        }
    }

    // Create a standalone fallback device (editor/batch mode before UnityPluginLoad).
    {
        std::lock_guard<std::mutex> lk(s_dxgiMutex);
        if (s_dxgiMgr) return true;  // double-checked

        if (!s_fallbackDevice)
        {
            D3D_FEATURE_LEVEL fl;
            D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
                              D3D11_CREATE_DEVICE_VIDEO_SUPPORT,
                              nullptr, 0, D3D11_SDK_VERSION,
                              &s_fallbackDevice, &fl, nullptr);
        }
        if (!s_fallbackDevice) return false;

        MFCreateDXGIDeviceManager(&s_dxgiToken, &s_dxgiMgr);
        if (s_dxgiMgr) s_dxgiMgr->ResetDevice(s_fallbackDevice, s_dxgiToken);
        return s_dxgiMgr != nullptr;
    }
}

static std::string s_globalError;

// ── MF init / teardown ────────────────────────────────────────────────────────

static bool EnsureMF()
{
    if (s_mfStarted) return true;
    HRESULT hr = MFStartup(MF_VERSION, MFSTARTUP_NOSOCKET);
    if (FAILED(hr)) { s_globalError = "MFStartup failed"; return false; }
    s_mfStarted = true;
    return true;
}

// Called from DllMain on PROCESS_DETACH
static void ShutdownMF()
{
    // NOTE: Do NOT call MFShutdown() here.
    // When Unity stops play mode with domain reload it unloads this DLL, triggering
    // DLL_PROCESS_DETACH.  If any IMFTransform is still alive at that moment,
    // MFShutdown() blocks forever waiting for MF work-queue threads to drain.
    // This is the root cause of the Unity Editor freeze on play-mode stop.
    // Windows will clean up MF COM objects when the process terminates.
    s_mfStarted = false;
}

BOOL WINAPI DllMain(HINSTANCE, DWORD reason, LPVOID)
{
    // Do NOT call CoInitializeEx/CoUninitialize here — Unity owns COM on all its threads.
    // Calling CoUninitialize from DLL_PROCESS_DETACH can deadlock during play-mode exit.
    if (reason == DLL_PROCESS_DETACH) ShutdownMF();
    return TRUE;
}

// ── Configure MFT ─────────────────────────────────────────────────────────────

// Try to create the MFT and negotiate H264→fmt output type.
static HRESULT TryConfigureOutput(IMFTransform* mft, const GUID& outSubtype, OutFmt fmt,
                                   int width, int height, OutFmt& chosenFmt)
{
    IMFMediaType* outType = nullptr;
    HRESULT hr = MFCreateMediaType(&outType);
    if (FAILED(hr)) return hr;

    outType->SetGUID(MF_MT_MAJOR_TYPE,    MFMediaType_Video);
    outType->SetGUID(MF_MT_SUBTYPE,       outSubtype);
    if (width > 0 && height > 0)
        MFSetAttributeSize(outType, MF_MT_FRAME_SIZE, width, height);

    hr = mft->SetOutputType(0, outType, 0);
    outType->Release();

    if (SUCCEEDED(hr)) chosenFmt = fmt;
    return hr;
}

static bool ConfigureDecoder(Decoder& d)
{
    if (!EnsureMF()) return false;

    // ── Hardware DXVA decode (primary path) ───────────────────────────────────
    // Uses the DXGI device manager pointing at Unity's device (or fallback device).
    d.mft_usesD3D = EnsureDXGIManager() && (s_dxgiMgr != nullptr);
    if (!d.mft_usesD3D)
        d.lastError = "DXVA unavailable — falling back to software decode";

    // ── Create MFT ────────────────────────────────────────────────────────────
    static const GUID kCLSID_MSH264Decoder =
        {0x62CE7E72,0x4C71,0x4D20,{0xB1,0x5D,0x45,0x28,0x31,0xA8,0x7D,0x9D}};

    HRESULT hr = CoCreateInstance(kCLSID_MSH264Decoder, nullptr,
                                  CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&d.mft));
    if (FAILED(hr)) { d.lastError = "CoCreateInstance H264 MFT failed"; return false; }

    // ── Low-latency mode: disables B-frame reorder buffering ──────────────────
    // Without this the MFT holds ~1 second of frames internally before outputting.
    {
        IMFAttributes* attrs = nullptr;
        if (SUCCEEDED(d.mft->GetAttributes(&attrs)))
        {
            // MF_LOW_LATENCY = {9c27891a-ed7a-40e1-88e8-b22727a024ee}
            static const GUID MF_LOW_LATENCY_GUID =
                {0x9c27891a,0xed7a,0x40e1,{0x88,0xe8,0xb2,0x27,0x27,0xa0,0x24,0xee}};
            attrs->SetUINT32(MF_LOW_LATENCY_GUID, TRUE);
            attrs->Release();
        }
    }

    // Attach shared D3D11 device for hardware DXVA decode (skipped on software path).
    if (d.mft_usesD3D)
        d.mft->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, (ULONG_PTR)s_dxgiMgr);

    // ── Set input type: H264 ──────────────────────────────────────────────────
    IMFMediaType* inType = nullptr;
    MFCreateMediaType(&inType);
    inType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    inType->SetGUID(MF_MT_SUBTYPE,    MFVideoFormat_H264);
    inType->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
    if (d.width > 0 && d.height > 0)
        MFSetAttributeSize(inType, MF_MT_FRAME_SIZE, d.width, d.height);

    hr = d.mft->SetInputType(0, inType, 0);
    inType->Release();
    if (FAILED(hr)) { d.lastError = "SetInputType H264 failed"; return false; }

    // ── Set output type: prefer NV12 (hardware native), fall back to ARGB32 ──
    OutFmt chosen = OutFmt::Unknown;
    if (FAILED(TryConfigureOutput(d.mft, MFVideoFormat_NV12,   OutFmt::NV12, d.width, d.height, chosen)))
        TryConfigureOutput(d.mft, MFVideoFormat_ARGB32, OutFmt::BGRA, d.width, d.height, chosen);

    if (chosen == OutFmt::Unknown)
    { d.lastError = "No supported output format (tried NV12, ARGB32)"; return false; }
    d.outFmt = chosen;

    // ── Query actual row stride from the negotiated output type ───────────────
    // The MFT may pad rows to 16- or 64-byte alignment; using the wrong stride
    // causes a shifted/corrupted image during NV12→RGBA conversion.
    {
        IMFMediaType* outType = nullptr;
        if (SUCCEEDED(d.mft->GetOutputCurrentType(0, &outType)))
        {
            UINT32 stride32 = 0;
            if (FAILED(outType->GetUINT32(MF_MT_DEFAULT_STRIDE, &stride32)) || stride32 == 0)
                stride32 = (UINT32)((d.width + 15) & ~15); // safe fallback
            d.strideY = (int)stride32;
            outType->Release();
        }
        else
        {
            d.strideY = (d.width + 15) & ~15;
        }
    }

    // ── Start streaming ───────────────────────────────────────────────────────
    d.mft->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
    d.mft->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM,  0);

    if (d.width > 0 && d.height > 0)
        d.rgba.resize(d.width * d.height * 4, 0);

    d.ready = true;
    return true;
}

// ── Drain output buffers ───────────────────────────────────────────────────────

static void DrainOutputs(Decoder& d)
{
    while (true)
    {
        // Exit immediately if DeleteDecoder has been called — avoids holding decoderLock
        // while MFT/GPU work is still in flight (which would deadlock the delete path).
        if (d.aborting.load()) break;

        // Allocate output buffer
        MFT_OUTPUT_DATA_BUFFER outBuf = {};
        outBuf.dwStreamID = 0;

        // Check if MFT manages its own output buffers
        MFT_OUTPUT_STREAM_INFO streamInfo = {};
        d.mft->GetOutputStreamInfo(0, &streamInfo);
        bool mftAllocates = (streamInfo.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0;

        IMFSample*      sample = nullptr;
        IMFMediaBuffer* buf    = nullptr;

        if (!mftAllocates)
        {
            // We must allocate the output sample
            HRESULT hr2 = MFCreateSample(&sample);
            if (FAILED(hr2) || !sample) break;
            hr2 = MFCreateMemoryBuffer(streamInfo.cbSize, &buf);
            if (FAILED(hr2) || !buf) { sample->Release(); break; }
            sample->AddBuffer(buf);
            buf->Release();
            outBuf.pSample = sample;
        }

        DWORD status = 0;
        HRESULT hr   = d.mft->ProcessOutput(0, 1, &outBuf, &status);

        if (outBuf.pEvents) { outBuf.pEvents->Release(); outBuf.pEvents = nullptr; }

        if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT)
        {
            d.stat_drainNeedMore.fetch_add(1);
            // No more output available right now
            if (sample) sample->Release();
            break;
        }
        if (hr == MF_E_TRANSFORM_STREAM_CHANGE)
        {
            d.stat_drainStreamChg.fetch_add(1);
            // Output type has been INVALIDATED — we MUST re-negotiate using
            // GetOutputAvailableType + SetOutputType.  The old code called
            // GetOutputCurrentType which fails (type was cleared by MFT) and
            // never called SetOutputType, leaving the MFT with no output
            // format.  That caused all subsequent ProcessOutput to return
            // errors → no frames ever decoded.
            if (sample) { sample->Release(); }

            bool renegotiated = false;
            IMFMediaType* availType = nullptr;
            for (DWORD idx = 0; ; ++idx)
            {
                HRESULT avHr = d.mft->GetOutputAvailableType(0, idx, &availType);
                if (FAILED(avHr)) break;

                GUID subtype = {};
                availType->GetGUID(MF_MT_SUBTYPE, &subtype);

                bool acceptable = (subtype == MFVideoFormat_NV12)
                               || (subtype == MFVideoFormat_ARGB32);
                if (acceptable && SUCCEEDED(d.mft->SetOutputType(0, availType, 0)))
                {
                    d.outFmt = (subtype == MFVideoFormat_NV12) ? OutFmt::NV12 : OutFmt::BGRA;

                    UINT32 w = 0, h = 0;
                    MFGetAttributeSize(availType, MF_MT_FRAME_SIZE, &w, &h);
                    if (w > 0 && h > 0 && (d.width != (int)w || d.height != (int)h))
                    {
                        d.width  = (int)w;
                        d.height = (int)h;
                        d.rgba.resize(d.width * d.height * 4, 0);
                    }
                    UINT32 stride32 = 0;
                    if (FAILED(availType->GetUINT32(MF_MT_DEFAULT_STRIDE, &stride32)) || stride32 == 0)
                        stride32 = (UINT32)((d.width + 15) & ~15);
                    d.strideY = (int)stride32;

                    renegotiated = true;
                    availType->Release();
                    break;
                }
                availType->Release();
                availType = nullptr;
            }
            if (!renegotiated)
                d.lastError = "Stream change: failed to re-negotiate output type";
            continue;
        }
        if (FAILED(hr))
        {
            d.stat_drainFailed.fetch_add(1);
            if (sample) sample->Release();
            break;
        }

        // We have a decoded frame in outBuf.pSample
        IMFSample* outSample = outBuf.pSample;
        if (!outSample) break;

        // Update actual dimensions from sample attributes if not yet known
        UINT32 w = 0, h = 0;
        {
            IMFMediaType* curType = nullptr;
            if (SUCCEEDED(d.mft->GetOutputCurrentType(0, &curType)))
            {
                MFGetAttributeSize(curType, MF_MT_FRAME_SIZE, &w, &h);
                curType->Release();
            }
        }
        if (w == 0 || h == 0) { w = (UINT32)d.width; h = (UINT32)d.height; }
        if (w == 0 || h == 0) { outSample->Release(); continue; }

        if (d.width != (int)w || d.height != (int)h || d.rgba.empty())
        {
            d.width  = (int)w;
            d.height = (int)h;
            d.rgba.resize(d.width * d.height * 4, 0);
        }

        // ── Readback to RGBA32 ────────────────────────────────────────────────
        // For DXVA frames: QI the buffer for IMFDXGIBuffer, copy the GPU texture to
        // a CPU-readable staging texture, then Map it.  This replaces Lock2D which
        // returns DXGI_ERROR_WAIT_TIMEOUT when the GPU is busy with other work.
        // For software frames: fall through to Lock2D / 1D-lock as before.
        bool converted = false;
        bool releasedEarly = false; // true if DXVA path released firstBuf/outSample before Map
        IMFMediaBuffer* firstBuf = nullptr;
        if (SUCCEEDED(outSample->GetBufferByIndex(0, &firstBuf)))
        {
            // ── GPU path: hand NV12 surface to render thread ─────────────────
            // AddRef the DXVA surface so the MFT pool slot stays alive until
            // OnRenderEvent finishes the NV12→RGBA compute dispatch.
            // No staging texture, no Map(), no CPU copy.
            if (d.mft_usesD3D && s_unityDevice)
            {
                IMFDXGIBuffer* dxgiBuf = nullptr;
                if (SUCCEEDED(firstBuf->QueryInterface(IID_PPV_ARGS(&dxgiBuf))))
                {
                    ID3D11Texture2D* srcTex = nullptr;
                    UINT subIdx = 0;
                    if (SUCCEEDED(dxgiBuf->GetResource(IID_PPV_ARGS(&srcTex))) &&
                        SUCCEEDED(dxgiBuf->GetSubresourceIndex(&subIdx)))
                    {
                        // Pass ownership to render thread (GetResource already AddRef'd srcTex)
                        {
                            std::lock_guard<std::mutex> lk(d.nv12Mutex);
                            SafeRelease(&d.pendingNV12);
                            d.pendingNV12   = srcTex;
                            d.pendingSubIdx = subIdx;
                            d.nv12Dirty.store(true);
                        }
                        converted     = true;
                        releasedEarly = true;
                        d.stat_lock2dOK.fetch_add(1);
                        d.stat_drainProduced.fetch_add(1);
                        firstBuf->Release();  firstBuf  = nullptr;
                        outSample->Release(); outSample = nullptr;
                    }
                    else
                    {
                        if (srcTex) srcTex->Release();
                    }
                    dxgiBuf->Release();
                }
            }

            // ── Software fallback: Lock2D path ───────────────────────────────────
            // Skip if DXVA path already released firstBuf/outSample early.
            if (!converted && !releasedEarly)
            {
            // Prefer IMF2DBuffer — gives correct pitch for padded/aligned surfaces.
            IMF2DBuffer* buf2d = nullptr;
            if (SUCCEEDED(firstBuf->QueryInterface(IID_PPV_ARGS(&buf2d))))
            {
                // Get actual buffer byte count BEFORE locking, used to compute
                // the real padded Y-plane height for NV12 UV-plane offset.
                DWORD bufLen = 0;
                firstBuf->GetCurrentLength(&bufLen);

                BYTE* scanline0 = nullptr;
                LONG  pitch     = 0;
                HRESULT lkHr = buf2d->Lock2D(&scanline0, &pitch);
                if (SUCCEEDED(lkHr) && scanline0)
                {
                    int absPitch = (pitch < 0) ? -pitch : pitch;
                    if (d.outFmt == OutFmt::NV12)
                    {
                        // Hardware NV12 pads the Y plane to a 16/32/64 row boundary.
                        // UV follows at offset (absPitch * paddedYHeight), NOT (absPitch * d.height).
                        // Derive paddedYHeight from the real buffer size:
                        //   bufLen = absPitch * paddedYHeight * 3/2
                        //   paddedYHeight = bufLen * 2 / (absPitch * 3)
                        int paddedYHeight = (absPitch > 0 && bufLen > 0)
                            ? (int)((uint64_t)bufLen * 2 / ((uint64_t)absPitch * 3))
                            : (int)h;
                        if (paddedYHeight < (int)h) paddedYHeight = (int)h;

                        const uint8_t* yPlane  = (const uint8_t*)scanline0;
                        const uint8_t* uvPlane = yPlane + absPitch * paddedYHeight;
                        NV12ToRGBA(yPlane, absPitch, uvPlane, absPitch,
                                   d.rgba.data(), d.width, d.height);
                    }
                    else
                    {
                        BGRAToRGBA((const uint8_t*)scanline0, d.rgba.data(),
                                   d.width, d.height, absPitch);
                    }
                    buf2d->Unlock2D();
                    {
                        Decoder::ReadyFrame rf;
                        rf.width  = d.width;
                        rf.height = d.height;
                        rf.rgba.assign(d.rgba.begin(), d.rgba.end());
                        d.readyFrames.push_back(std::move(rf));
                        while (d.readyFrames.size() > Decoder::MaxReadyFrames)
                            d.readyFrames.pop_front();
                    }
                    d.stat_lock2dOK.fetch_add(1);
                    d.stat_drainProduced.fetch_add(1);
                    converted    = true;
                }
                else
                {
                    d.stat_lock2dFail.fetch_add(1);
                    d.lastError = "Lock2D failed hr=" + std::to_string(lkHr);
                }
                buf2d->Release();
            }

            // 1D lock fallback (rare: only if IMF2DBuffer QI fails)
            if (!converted)
            {
                BYTE* bufData = nullptr; DWORD bufLen = 0;
                if (SUCCEEDED(firstBuf->Lock(&bufData, nullptr, &bufLen)))
                {
                    if (d.outFmt == OutFmt::NV12)
                    {
                        int strideY = d.strideY > 0 ? d.strideY : (d.width + 15) & ~15;
                        // Same padded-height calculation as Lock2D path.
                        int paddedYHeight = (strideY > 0 && bufLen > 0)
                            ? (int)((uint64_t)bufLen * 2 / ((uint64_t)strideY * 3))
                            : d.height;
                        if (paddedYHeight < d.height) paddedYHeight = d.height;
                        NV12ToRGBA((const uint8_t*)bufData, strideY,
                                   (const uint8_t*)bufData + strideY * paddedYHeight, strideY,
                                   d.rgba.data(), d.width, d.height);
                    }
                    else
                    {
                        int stride = (d.width * 4 + 3) & ~3;
                        BGRAToRGBA(bufData, d.rgba.data(), d.width, d.height, stride);
                    }
                    firstBuf->Unlock();
                    {
                        Decoder::ReadyFrame rf;
                        rf.width  = d.width;
                        rf.height = d.height;
                        rf.rgba.assign(d.rgba.begin(), d.rgba.end());
                        d.readyFrames.push_back(std::move(rf));
                        while (d.readyFrames.size() > Decoder::MaxReadyFrames)
                            d.readyFrames.pop_front();
                    }
                    d.stat_lock1dOK.fetch_add(1);
                    d.stat_drainProduced.fetch_add(1);
                }
            }
            } // end software fallback block
            if (firstBuf) firstBuf->Release();
        }

        if (outSample) outSample->Release();
    }
}

// ── Public API ─────────────────────────────────────────────────────────────────

extern "C"
{

GVST_API int GVST_CreateDecoder()
{
    if (!EnsureMF()) return -1;
    int id = s_nextId.fetch_add(1);
    std::lock_guard<std::mutex> lk(s_mutex);
    s_decoders[id] = std::make_shared<Decoder>();
    return id;
}

GVST_API bool GVST_Initialize(int id, int width, int height)
{
    std::shared_ptr<Decoder> dec;
    {
        std::lock_guard<std::mutex> lk(s_mutex);
        auto it = s_decoders.find(id);
        if (it == s_decoders.end()) return false;
        dec = it->second;
    }
    std::lock_guard<std::mutex> dlk(dec->decoderLock);
    dec->width  = width;
    dec->height = height;
    dec->readyFrames.clear();
    return ConfigureDecoder(*dec);
}

GVST_API bool GVST_Feed(int id, const unsigned char* data, int length)
{
    std::shared_ptr<Decoder> dec;
    {
        std::lock_guard<std::mutex> lk(s_mutex);
        auto it = s_decoders.find(id);
        if (it == s_decoders.end()) return false;
        dec = it->second;
    }
    std::lock_guard<std::mutex> dlk(dec->decoderLock);
    Decoder& d = *dec;
    if (!d.ready) return false;
    if (length <= 0) { d.lastError = "Feed: invalid length"; return false; }

    // Wrap NAL bytes in an IMFSample
    IMFSample*      sample = nullptr;
    IMFMediaBuffer* buf    = nullptr;

    HRESULT hr2 = MFCreateSample(&sample);
    if (FAILED(hr2) || !sample) { d.lastError = "MFCreateSample failed"; return false; }
    hr2 = MFCreateMemoryBuffer(length, &buf);
    if (FAILED(hr2) || !buf) { sample->Release(); d.lastError = "MFCreateMemoryBuffer failed"; return false; }

    BYTE* bufPtr    = nullptr;
    DWORD maxLen    = 0;
    buf->Lock(&bufPtr, &maxLen, nullptr);
    std::memcpy(bufPtr, data, length);
    buf->Unlock();
    buf->SetCurrentLength(length);

    sample->AddBuffer(buf);
    buf->Release();

    // Set monotonic PTS (MFT requires non-decreasing timestamps)
    sample->SetSampleTime(d.pts);
    sample->SetSampleDuration(333333); // ~30fps in 100ns units
    d.pts += 333333;

    HRESULT hr = d.mft->ProcessInput(0, sample, 0);

    if (hr == MF_E_NOTACCEPTING)
    {
        // Decoder output queue is full. Drain pending output first, then retry input once.
        DrainOutputs(d);
        if (d.aborting.load()) { sample->Release(); return false; }
        hr = d.mft->ProcessInput(0, sample, 0);
    }

    sample->Release();

    if (d.aborting.load()) return false;
    if (FAILED(hr)) { d.lastError = "ProcessInput failed hr=" + std::to_string(hr); return false; }

    d.stat_processInputOK.fetch_add(1);

    // Eagerly drain any output that became available
    DrainOutputs(d);
    return true;
}

GVST_API bool GVST_GetFrame(int id, unsigned char* outRgba, int bufferSize,
                            int* outWidth, int* outHeight)
{
    std::shared_ptr<Decoder> dec;
    {
        std::lock_guard<std::mutex> lk(s_mutex);
        auto it = s_decoders.find(id);
        if (it == s_decoders.end()) return false;
        dec = it->second;
    }
    std::lock_guard<std::mutex> dlk(dec->decoderLock);
    Decoder& d = *dec;
    if (d.readyFrames.empty()) { d.stat_getFrameEmpty.fetch_add(1); return false; }

    const Decoder::ReadyFrame& rf = d.readyFrames.front();

    // Always report the actual decoded dimensions so the caller can reallocate.
    if (outWidth)  *outWidth  = rf.width;
    if (outHeight) *outHeight = rf.height;

    int needed = rf.width * rf.height * 4;
    if (outRgba && bufferSize >= needed)
    {
        std::memcpy(outRgba, rf.rgba.data(), needed);
        d.readyFrames.pop_front();
        d.stat_getFrameOK.fetch_add(1);
        return true;
    }

    d.stat_getFrameBufSmall.fetch_add(1);
    return false;
}

GVST_API void GVST_DeleteDecoder(int id)
{
    // Step 1: remove from map so no new Feed/GetFrame calls can find this decoder.
    // Do this under s_mutex only (brief).
    std::shared_ptr<Decoder> dec;
    {
        std::lock_guard<std::mutex> lk(s_mutex);
        auto it = s_decoders.find(id);
        if (it == s_decoders.end()) return;
        dec = std::move(it->second);
        s_decoders.erase(it);
    }

    // Step 2: signal abort so DrainOutputs exits its loop at the top.
    dec->aborting.store(true);

    // Step 3: acquire decoderLock (worker will finish quickly because:
    //   - aborting flag causes exit at top of DrainOutputs loop
    //   - Lock2D on a DXGI sample takes a few ms at most — not indefinite)
    // Then flush the MFT (must be under same lock as ProcessInput/ProcessOutput
    // because MFT is NOT thread-safe — calling flush without the lock crashed Unity)
    // and release all resources.
    {
        std::lock_guard<std::mutex> dlk(dec->decoderLock);
        Decoder& d = *dec;
        if (d.mft)
        {
            d.mft->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0);
            d.mft->Release();
            d.mft = nullptr;
        }
        // GPU resources (outputTex, uav, srvY, srvUV, pendingNV12) are NOT released
        // here.  They are owned by the Decoder destructor, which runs when the last
        // shared_ptr drops.  OnRenderEvent holds shared_ptrs via its snapshot but
        // checks aborting==true before accessing GPU resources, so the destructor
        // cannot run concurrently with a CS dispatch on those resources.
        {
            std::lock_guard<std::mutex> lk(d.nv12Mutex);
            d.nv12Dirty.store(false);
            // pendingNV12 will be released by the destructor
        }
    }
    // shared_ptr ref-count drops to 0 here — Decoder destructor releases GPU resources
}

/// <summary>
/// Signal a decoder to abort its current DrainOutputs loop without acquiring decoderLock.
/// Call this before joining a worker thread that may be blocked in GVST_Feed.
/// The decoder remains valid — call GVST_DeleteDecoder later for full cleanup.
/// </summary>
GVST_API void GVST_AbortDecoder(int id)
{
    std::shared_ptr<Decoder> dec;
    {
        std::lock_guard<std::mutex> lk(s_mutex);
        auto it = s_decoders.find(id);
        if (it == s_decoders.end()) return;
        dec = it->second;
    }
    dec->aborting.store(true);
}

GVST_API const char* GVST_GetError(int id)
{
    static thread_local std::string buf;
    if (id < 0) { buf = s_globalError; return buf.c_str(); }
    std::shared_ptr<Decoder> dec;
    {
        std::lock_guard<std::mutex> lk(s_mutex);
        auto it = s_decoders.find(id);
        if (it == s_decoders.end()) { buf.clear(); return buf.c_str(); }
        dec = it->second;
    }
    // Copy under lock to avoid dangling c_str() from concurrent lastError mutation.
    std::lock_guard<std::mutex> dlk(dec->decoderLock);
    buf = dec->lastError;
    return buf.c_str();
}

/// <summary>
/// Return diagnostic counters for a decoder as a human-readable string.
/// Thread-safe (atomics only), no lock needed.
/// </summary>
GVST_API const char* GVST_GetStats(int id)
{
    static thread_local std::string buf;
    std::shared_ptr<Decoder> dec;
    {
        std::lock_guard<std::mutex> lk(s_mutex);
        auto it = s_decoders.find(id);
        if (it == s_decoders.end()) { buf = "decoder not found"; return buf.c_str(); }
        dec = it->second;
    }
    Decoder& d = *dec;

    // Try to acquire lock briefly for non-atomic field reads.
    // If the worker holds it (Feed in progress), return stale stats rather than blocking.
    std::string readyQ, res, dxva, err;
    {
        std::unique_lock<std::mutex> dlk(d.decoderLock, std::try_to_lock);
        if (dlk.owns_lock())
        {
            readyQ = std::to_string(d.readyFrames.size());
            res    = std::to_string(d.width) + "x" + std::to_string(d.height);
            dxva   = std::to_string(d.mft_usesD3D ? 1 : 0);
            err    = d.lastError;
        }
        else
        {
            readyQ = "?"; res = "?"; dxva = "?"; err = "busy";
        }
    }

    buf = "inputOK=" + std::to_string(d.stat_processInputOK.load())
        + " produced=" + std::to_string(d.stat_drainProduced.load())
        + " needMore=" + std::to_string(d.stat_drainNeedMore.load())
        + " streamChg=" + std::to_string(d.stat_drainStreamChg.load())
        + " drainFail=" + std::to_string(d.stat_drainFailed.load())
        + " lock2dOK=" + std::to_string(d.stat_lock2dOK.load())
        + " lock2dFail=" + std::to_string(d.stat_lock2dFail.load())
        + " lock1dOK=" + std::to_string(d.stat_lock1dOK.load())
        + " mapTimeout=" + std::to_string(d.stat_mapTimeout.load())
        + " getFrameOK=" + std::to_string(d.stat_getFrameOK.load())
        + " getFrameBufSmall=" + std::to_string(d.stat_getFrameBufSmall.load())
        + " getFrameEmpty=" + std::to_string(d.stat_getFrameEmpty.load())
        + " readyQ=" + readyQ
        + " fmt=" + (d.outFmt == OutFmt::NV12 ? "NV12" : d.outFmt == OutFmt::BGRA ? "BGRA" : "?")
        + " res=" + res
        + " dxva=" + dxva
        + " err=\"" + err + "\"";
    return buf.c_str();
}

// ── Zero-copy render callback ──────────────────────────────────────────────────
// Called by Unity's render thread via GL.IssuePluginEvent(GVST_GetRenderCallback(), 0).
// For every decoder with nv12Dirty: grabs the pending NV12 DXVA surface,
// creates per-frame SRVs (Y plane R8_UNORM + UV plane R8G8_UNORM), dispatches
// the NV12→RGBA compute shader into outputTex, then releases the DXVA surface.
// ZERO CPU pixel work. ZERO staging texture. ZERO Map().
static void UNITY_INTERFACE_API OnRenderEvent(int /*eventId*/)
{
    ID3D11Device* dev = s_unityDevice;
    if (!dev) return;
    if (!EnsureNV12ComputeShader(dev)) return;

    // Snapshot decoder list without holding s_mutex across D3D work.
    std::vector<std::shared_ptr<Decoder>> snapshot;
    {
        std::lock_guard<std::mutex> lk(s_mutex);
        snapshot.reserve(s_decoders.size());
        for (auto& kv : s_decoders)
            snapshot.push_back(kv.second);
    }

    ID3D11DeviceContext* ctx = nullptr;
    dev->GetImmediateContext(&ctx);
    if (!ctx) return;

    for (auto& dec : snapshot)
    {
        // Skip decoders being torn down — their GPU resources may be freeing
        // in the destructor after this loop iteration (safe due to shared_ptr
        // ref held by snapshot, but aborting guards the logical check).
        if (dec->aborting.load()) continue;
        if (!dec->nv12Dirty.load()) continue;

        // Take ownership of the pending NV12 surface.
        ID3D11Texture2D* nv12 = nullptr;
        UINT subIdx = 0;
        {
            std::lock_guard<std::mutex> lk(dec->nv12Mutex);
            if (!dec->nv12Dirty.load() || !dec->pendingNV12) continue;
            nv12   = dec->pendingNV12;    // ref owned by us now
            subIdx = dec->pendingSubIdx;
            dec->pendingNV12 = nullptr;
            dec->nv12Dirty.store(false);
        }

        // Query actual dimensions from the NV12 surface.
        D3D11_TEXTURE2D_DESC nd = {};
        nv12->GetDesc(&nd);
        int w = (int)nd.Width, h = (int)nd.Height;
        if (w <= 0 || h <= 0) { nv12->Release(); continue; }

        // Create / resize RGBA32 output texture (BIND_SHADER_RESOURCE | UAV).
        if (!dec->outputTex || dec->outputTexW != w || dec->outputTexH != h)
        {
            SafeRelease(&dec->uav);
            SafeRelease(&dec->srvY);
            SafeRelease(&dec->srvUV);
            SafeRelease(&dec->outputTex);
            dec->outputTexW = 0;
            dec->outputTexH = 0;

            D3D11_TEXTURE2D_DESC outD = {};
            outD.Width            = (UINT)w;
            outD.Height           = (UINT)h;
            outD.MipLevels        = 1;
            outD.ArraySize        = 1;
            outD.Format           = DXGI_FORMAT_R8G8B8A8_UNORM;
            outD.SampleDesc.Count = 1;
            outD.Usage            = D3D11_USAGE_DEFAULT;
            outD.BindFlags        = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;
            HRESULT hr = dev->CreateTexture2D(&outD, nullptr, &dec->outputTex);
            if (FAILED(hr)) { nv12->Release(); continue; }

            D3D11_UNORDERED_ACCESS_VIEW_DESC uavD = {};
            uavD.Format        = DXGI_FORMAT_R8G8B8A8_UNORM;
            uavD.ViewDimension = D3D11_UAV_DIMENSION_TEXTURE2D;
            if (FAILED(dev->CreateUnorderedAccessView(dec->outputTex, &uavD, &dec->uav)))
            { SafeRelease(&dec->outputTex); nv12->Release(); continue; }

            dec->outputTexW = w;
            dec->outputTexH = h;
            dec->publishedOutputTex.store(dec->outputTex);
        }

        // Build SRVs for this specific NV12 subresource.
        // NV12 D3D11 plane access: R8_UNORM → plane 0 (Y), R8G8_UNORM → plane 1 (UV).
        SafeRelease(&dec->srvY);
        SafeRelease(&dec->srvUV);
        {
            D3D11_SHADER_RESOURCE_VIEW_DESC srvD = {};
            srvD.ViewDimension                    = D3D11_SRV_DIMENSION_TEXTURE2DARRAY;
            srvD.Texture2DArray.MipLevels         = 1;
            srvD.Texture2DArray.ArraySize         = 1;
            srvD.Texture2DArray.FirstArraySlice   = subIdx;
            srvD.Texture2DArray.MostDetailedMip   = 0;

            srvD.Format = DXGI_FORMAT_R8_UNORM;
            dev->CreateShaderResourceView(nv12, &srvD, &dec->srvY);

            srvD.Format = DXGI_FORMAT_R8G8_UNORM;
            dev->CreateShaderResourceView(nv12, &srvD, &dec->srvUV);
        }

        if (!dec->srvY || !dec->srvUV) { nv12->Release(); continue; }

        // Dispatch NV12→RGBA compute shader.
        UINT groupsX = ((UINT)w + 7) / 8;
        UINT groupsY = ((UINT)h + 7) / 8;

        ID3D11ShaderResourceView* srvs[2] = { dec->srvY, dec->srvUV };
        ctx->CSSetShader(s_nv12Cs, nullptr, 0);
        ctx->CSSetShaderResources(0, 2, srvs);
        ctx->CSSetUnorderedAccessViews(0, 1, &dec->uav, nullptr);
        ctx->Dispatch(groupsX, groupsY, 1);

        // Unbind — prevent read/write hazards for Unity's subsequent render passes.
        ID3D11ShaderResourceView* nullSRVs[2] = {};
        ID3D11UnorderedAccessView* nullUAV = nullptr;
        ctx->CSSetShaderResources(0, 2, nullSRVs);
        ctx->CSSetUnorderedAccessViews(0, 1, &nullUAV, nullptr);
        ctx->CSSetShader(nullptr, nullptr, 0);

        // Release DXVA surface — returns the pool slot to the MFT.
        nv12->Release();
    }

    ctx->Release();
}

/// Returns a function pointer to OnRenderEvent as an intptr_t.
/// C# passes this to GL.IssuePluginEvent(ptr, 0) to schedule per-frame GPU upload.
GVST_API long long GVST_GetRenderCallback()
{
    return (long long)(void*)static_cast<void(UNITY_INTERFACE_API*)(int)>(OnRenderEvent);
}

/// Returns the native ID3D11Texture2D* for the decoder's zero-copy output texture.
/// 0 until the first frame has been processed by the render callback.
/// C# casts this to IntPtr and passes it to Texture2D.CreateExternalTexture.
/// When the decoded resolution changes the pointer changes — C# must call
/// UpdateExternalTexture(newPtr) or recreate the Texture2D.
GVST_API long long GVST_GetTexturePtr(int id)
{
    std::shared_ptr<Decoder> dec;
    {
        std::lock_guard<std::mutex> lk(s_mutex);
        auto it = s_decoders.find(id);
        if (it == s_decoders.end()) return 0;
        dec = it->second;
    }
    // Read the atomically-published pointer (written by OnRenderEvent after create/resize).
    return (long long)(void*)dec->publishedOutputTex.load();
}

} // extern "C"
