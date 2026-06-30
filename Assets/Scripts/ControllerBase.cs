using UnityEngine;

public abstract class ControllerBase : MonoBehaviour
{
    public int playerID;
    protected Player mountedPlayer;

    void Start()
    {
        PlayerManager.instance.HandlePlayerSpawn(this);
        OnStart();
    }

    protected virtual void OnStart() { }

    public void MountToPlayer(Player player)
    {
        mountedPlayer = player;
    }

    public void PushInput(InputData data)
    {
        if (mountedPlayer)
            mountedPlayer.input = data;
    }
}
