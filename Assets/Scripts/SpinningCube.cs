using UnityEngine;

// Spins the object and smoothly cycles its color through the rainbow.
public class SpinningCube : MonoBehaviour
{
    public Vector3 rotationSpeed = new Vector3(30f, 60f, 15f);
    public float colorCycleSpeed = 0.2f;
    public float bobHeight = 0.25f;
    public float bobSpeed = 2f;

    private Renderer rend;
    private Vector3 startPos;

    void Start()
    {
        rend = GetComponent<Renderer>();
        startPos = transform.position;
    }

    void Update()
    {
        transform.Rotate(rotationSpeed * Time.deltaTime);
        transform.position = startPos + Vector3.up * Mathf.Sin(Time.time * bobSpeed) * bobHeight;

        if (rend != null)
        {
            float hue = Mathf.Repeat(Time.time * colorCycleSpeed, 1f);
            rend.material.color = Color.HSVToRGB(hue, 0.8f, 1f);
        }
    }
}
