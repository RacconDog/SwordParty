using UnityEngine;
using UnityEngine.SceneManagement;

// Debug-only chords read through the legacy Input Manager so they work
// without touching the gameplay Input System actions.
public class DebugShortcuts : MonoBehaviour
{
    [SerializeField] private MenuManager menuManager;

    [Header("Chord (PS layout on Windows)")]
    [SerializeField] private KeyCode startButton = KeyCode.JoystickButton9;    // Options / Start
    [SerializeField] private KeyCode triangleButton = KeyCode.JoystickButton3; // Triangle

    // Static so it survives the scene reload; the fresh DebugShortcuts
    // instance sees it and jumps straight back into a local session.
    private static bool autoStartLocal;

    void Start()
    {
        if (autoStartLocal)
        {
            autoStartLocal = false;
            menuManager.StartLocalSession();
        }
    }

    void Update()
    {
        // Fire once when the second button of the chord comes down.
        bool chord =
            (Input.GetKey(startButton) && Input.GetKeyDown(triangleButton)) ||
            (Input.GetKeyDown(startButton) && Input.GetKey(triangleButton));

        if (chord)
        {
            // Tear down any running session (local host, relay host, or
            // client) so the fresh scene can StartHost cleanly.
            if (Unity.Netcode.NetworkManager.Singleton &&
                Unity.Netcode.NetworkManager.Singleton.IsListening)
                Unity.Netcode.NetworkManager.Singleton.Shutdown();

            autoStartLocal = true;
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        }
    }
}
