using UnityEngine;

public class Player : MonoBehaviour
{
    public InputData input;

    void Update()
    {
        if (input.attack)
        {
            print("hit: " + input.id);
        }
    }
}