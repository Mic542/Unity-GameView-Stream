using UnityEngine;

public class MoveLeftRight : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        // ping pong left right movement
        float pingPong = Mathf.PingPong(Time.time, 1) * 2 - 1;
        transform.Translate(pingPong * Time.deltaTime, 0, 0);
    }
}
