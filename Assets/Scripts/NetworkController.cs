using UnityEngine;
using Unity.Netcode;

public class NetworkController : NetworkBehaviour
{
    // Attached to the network player object on the host side.
    // The owning client calls SendInputServerRpc each frame;
    // the host applies it to the mounted Player.

    private ControllerBase controller;

    public override void OnNetworkSpawn()
    {
        controller = GetComponent<ControllerBase>();
    }

    void Update()
    {
        if (!IsOwner) return;

        // Owner reads local hardware and ships raw values to the host.
        var input = GatherLocalInput();
        SendInputServerRpc(input);
    }

    private NetworkInputData GatherLocalInput()
    {
        // If you want to reuse Unity's Input System here, inject a PlayerInput
        // reference. For now this is a stub you can fill in per-project.
        return new NetworkInputData();
    }

    [ServerRpc]
    private void SendInputServerRpc(NetworkInputData raw)
    {
        var data = new InputData
        {
            id = controller.playerID,
            move = raw.move,
            look = raw.look,
            dash = raw.dash,
            attack = raw.attack,
            throwAttack = raw.throwAttack,
        };

        controller.PushInput(data);
    }
}

// Netcode structs must be INetworkSerializable.
public struct NetworkInputData : INetworkSerializable
{
    public UnityEngine.Vector2 move;
    public UnityEngine.Vector2 look;
    public bool dash;
    public bool attack;
    public bool throwAttack;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref move);
        serializer.SerializeValue(ref look);
        serializer.SerializeValue(ref dash);
        serializer.SerializeValue(ref attack);
        serializer.SerializeValue(ref throwAttack);
    }
}
