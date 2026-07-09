using TMPro;
using UnityEngine;

public class MenuManager : MonoBehaviour
{
    [Header("Menu References")]
    [SerializeField] private GameObject gameParent;
    [SerializeField] private GameObject menuParent;
    [SerializeField] private TMP_InputField joinCodeField;
    [SerializeField] private RelayManager relayManager;

    public void StartLocalSession()
    {
        print("Starting Local Game");

        // PlayerManager owns join handling + mounting, so it survives this
        // menu being deactivated below.
        PlayerManager.instance.BeginLocalSession();

        menuParent.SetActive(false);
        gameParent.SetActive(true);
    }

    public void HostOnlineSession()
    {
        PlayerManager.instance.ChangeGameState(PlayerManager.GameState.OnlineHost);
        relayManager.CreateRelay();

        menuParent.SetActive(false);
        gameParent.SetActive(true);
    }

    public void JoinOnlineSession()
    {
        PlayerManager.instance.ChangeGameState(PlayerManager.GameState.OnlineJoin);
        relayManager.JoinRelay(joinCodeField.text);

        menuParent.SetActive(false);
        gameParent.SetActive(true);
    }
}
