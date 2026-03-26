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
#include <dxgi.h>

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
            int rdiff =  1434 * V;
            int gdiff = -352  * U - 731 * V;
            int bdiff =  1809 * U;

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

// ── Decoder state ──────────────────────────────────────────────────────────────

enum class OutFmt { Unknown, NV12, BGRA };

struct Decoder
{
    IMFTransform*         mft         = nullptr;
    ID3D11Device*         d3dDevice   = nullptr;   // kept alive for MFT hardware decode
    IMFDXGIDeviceManager* dxgiMgr     = nullptr;   // kept alive for MFT hardware decode
    UINT                  dxgiToken   = 0;
    std::atomic<bool>     aborting    { false };    // set by DeleteDecoder before acquiring decoderLock
    int   width    = 0;
    int   height   = 0;
    int   strideY  = 0;
    bool  ready    = false;
    OutFmt outFmt  = OutFmt::Unknown;
    LONGLONG pts   = 0;

    std::vector<uint8_t> rgba;
    bool  frameReady  = false;
    std::string lastError;

    std::mutex decoderLock;
};

static std::unordered_map<int, std::shared_ptr<Decoder>> s_decoders;
static std::mutex   s_mutex;  // guards s_decoders map only — NOT held during MFT/D3D work
static std::atomic<int> s_nextId{1};

static bool s_mfStarted = false;

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

    // ── D3D11 device for hardware video decode ────────────────────────────────
    {
        UINT flags = D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
        D3D_FEATURE_LEVEL fl;
        // Note: we don't retain the immediate context here.
        // All GPU operations go through IMFDXGIDeviceManager::LockDevice which
        // returns the context under exclusive lock with the MFT.
        HRESULT hr = D3D11CreateDevice(
            nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags,
            nullptr, 0, D3D11_SDK_VERSION,
            &d.d3dDevice, &fl, nullptr);
        if (SUCCEEDED(hr))
        {
            hr = MFCreateDXGIDeviceManager(&d.dxgiToken, &d.dxgiMgr);
            if (SUCCEEDED(hr))
                d.dxgiMgr->ResetDevice(d.d3dDevice, d.dxgiToken);
        }
        if (FAILED(hr))
        { SafeRelease(&d.d3dDevice); SafeRelease(&d.dxgiMgr); }
    }

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

    // ── Attach D3D11 device manager → hardware video engine ──────────────────
    if (d.dxgiMgr)
        d.mft->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER,
                              reinterpret_cast<ULONG_PTR>(d.dxgiMgr));

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

    // ── Set output type: NV12 (hardware path), fall back to ARGB32 ────────────
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
            MFCreateSample(&sample);
            MFCreateMemoryBuffer(streamInfo.cbSize, &buf);
            sample->AddBuffer(buf);
            buf->Release();
            outBuf.pSample = sample;
        }

        DWORD status = 0;
        HRESULT hr   = d.mft->ProcessOutput(0, 1, &outBuf, &status);

        if (outBuf.pEvents) { outBuf.pEvents->Release(); outBuf.pEvents = nullptr; }

        if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT)
        {
            // No more output available right now
            if (sample) sample->Release();
            break;
        }
        if (hr == MF_E_TRANSFORM_STREAM_CHANGE)
        {
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
        // Use IMF2DBuffer::Lock2D for both GPU (DXGI) and CPU (system memory) samples.
        // MF handles GPU→CPU readback internally with correct device serialization.
        // This eliminates all manual LockDevice / staging-texture / CopySubresource
        // Map code that caused slowness (retry loops) and crashes (thread-unsafe MFT).
        bool converted = false;
        IMFMediaBuffer* firstBuf = nullptr;
        if (SUCCEEDED(outSample->GetBufferByIndex(0, &firstBuf)))
        {
            // Prefer IMF2DBuffer — gives correct pitch for padded/aligned surfaces.
            IMF2DBuffer* buf2d = nullptr;
            if (SUCCEEDED(firstBuf->QueryInterface(IID_PPV_ARGS(&buf2d))))
            {
                BYTE* scanline0 = nullptr;
                LONG  pitch     = 0;
                HRESULT lkHr = buf2d->Lock2D(&scanline0, &pitch);
                if (SUCCEEDED(lkHr) && scanline0)
                {
                    int absPitch = (pitch < 0) ? -pitch : pitch;
                    if (d.outFmt == OutFmt::NV12)
                    {
                        const uint8_t* yPlane  = (const uint8_t*)scanline0;
                        const uint8_t* uvPlane = yPlane + absPitch * (int)h;
                        NV12ToRGBA(yPlane, absPitch, uvPlane, absPitch,
                                   d.rgba.data(), d.width, d.height);
                    }
                    else
                    {
                        BGRAToRGBA((const uint8_t*)scanline0, d.rgba.data(),
                                   d.width, d.height, (pitch < 0) ? -pitch : pitch);
                    }
                    buf2d->Unlock2D();
                    d.frameReady = true;
                    converted    = true;
                }
                else
                {
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
                        NV12ToRGBA((const uint8_t*)bufData, strideY,
                                   (const uint8_t*)bufData + strideY * d.height, strideY,
                                   d.rgba.data(), d.width, d.height);
                    }
                    else
                    {
                        int stride = (d.width * 4 + 3) & ~3;
                        BGRAToRGBA(bufData, d.rgba.data(), d.width, d.height, stride);
                    }
                    firstBuf->Unlock();
                    d.frameReady = true;
                }
            }
            firstBuf->Release();
        }

        outSample->Release();
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

    // Wrap NAL bytes in an IMFSample
    IMFSample*      sample = nullptr;
    IMFMediaBuffer* buf    = nullptr;

    MFCreateSample(&sample);
    MFCreateMemoryBuffer(length, &buf);

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
    sample->Release();

    if (FAILED(hr)) { d.lastError = "ProcessInput failed"; return false; }

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
    if (!d.frameReady || d.rgba.empty()) return false;

    // Always report the actual decoded dimensions so the caller can reallocate.
    if (outWidth)  *outWidth  = d.width;
    if (outHeight) *outHeight = d.height;

    int needed = d.width * d.height * 4;
    if (outRgba && bufferSize >= needed)
    {
        std::memcpy(outRgba, d.rgba.data(), needed);
        d.frameReady = false;
        return true;
    }

    // Buffer too small — return true with dimensions set but no copy.
    // Caller should reallocate and call again.  Frame stays ready.
    // Actually return false so caller knows no pixels were written.
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
        SafeRelease(&d.dxgiMgr);
        SafeRelease(&d.d3dDevice);
    }
    // shared_ptr ref-count drops to 0 here — Decoder destroyed
}

GVST_API const char* GVST_GetError(int id)
{
    if (id < 0) return s_globalError.c_str();
    std::shared_ptr<Decoder> dec;
    {
        std::lock_guard<std::mutex> lk(s_mutex);
        auto it = s_decoders.find(id);
        if (it == s_decoders.end()) return "";
        dec = it->second;
    }
    // lastError is only written under decoderLock, but reading here is best-effort
    return dec->lastError.c_str();
}

} // extern "C"
