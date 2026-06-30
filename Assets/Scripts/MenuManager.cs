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


        menuParent.SetActive(false);
        gameParent.SetActive(true);
    }

    public void HostOnlineSession()
    {
        relayManager.CreateRelay();

        menuParent.SetActive(false);
        gameParent.SetActive(true);
    }
 
    public void JoinOnlineSession()
    {
        relayManager.JoinRelay(joinCodeField.text);

        menuParent.SetActive(false);
        gameParent.SetActive(true);
    }
}
