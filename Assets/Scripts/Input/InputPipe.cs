using UnityEngine;

// A small input sink that each controller owns. Controllers push InputData
// into it, and it forwards that data to whatever Player it's mounted to.
// The Player never knows which kind of controller is driving it.
public class InputPipe
{
    public int playerID;
    private Player mountedPlayer;

    public bool IsMounted => mountedPlayer != null;

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
