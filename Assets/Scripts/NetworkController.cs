using Unity.Netcode;
using Unity.Collections;
using System.Collections.Generic;

// The network half of the input piping, built on Netcode's named messages so
// it needs NO scene object and NO prefab — PlayerManager calls HostSession()
// whenever a session starts (local or online) and everything self-wires.
//
// Pads on client machines register here and stream their input up; the
// server creates a host-side InputPipe per (client, pad) and pushes into it,
// so remote pads and couch pads are indistinguishable to the simulation.
// Host-side pads never touch this — they push into their pipes directly.
public static class NetworkController
{
    private const string RegisterMsg = "SwordParty.RegisterPad";
    private const string InputMsg = "SwordParty.PadInput";

    // Server-side lookup: one pipe + avatar per pad per connected machine.
    private static readonly Dictionary<(ulong clientId, int padId), InputPipe> pipes
        = new Dictionary<(ulong, int), InputPipe>();
    private static readonly Dictionary<(ulong clientId, int padId), Player> avatars
        = new Dictionary<(ulong, int), Player>();

    // ---------- server side ----------

    // Call on every server start; handlers don't survive a session shutdown.
    public static void HostSession()
    {
        pipes.Clear();
        avatars.Clear();

        var messenger = NetworkManager.Singleton.CustomMessagingManager;
        messenger.RegisterNamedMessageHandler(RegisterMsg, OnRegisterPad);
        messenger.RegisterNamedMessageHandler(InputMsg, OnPadInput);

        // -= before += so repeated sessions never double-subscribe.
        NetworkManager.Singleton.OnClientDisconnectCallback -= HandleClientDisconnect;
        NetworkManager.Singleton.OnClientDisconnectCallback += HandleClientDisconnect;
    }

    private static void OnRegisterPad(ulong senderClientId, FastBufferReader payload)
    {
        payload.ReadValueSafe(out int padId);

        var key = (senderClientId, padId);
        if (pipes.ContainsKey(key)) return;

        var pipe = new InputPipe();
        pipes[key] = pipe;
        avatars[key] = PlayerManager.instance.HandlePlayerSpawn(pipe);
    }

    private static void OnPadInput(ulong senderClientId, FastBufferReader payload)
    {
        payload.ReadValueSafe(out int padId);
        payload.ReadNetworkSerializable(out NetworkInputData raw);

        if (!pipes.TryGetValue((senderClientId, padId), out InputPipe pipe))
            return;

        pipe.PushInput(new InputData
        {
            id = pipe.playerID,
            move = raw.move,
            look = raw.look,
            dash = raw.dash,
            attack = raw.attack,
            throwAttack = raw.throwAttack,
        });
    }

    private static void HandleClientDisconnect(ulong clientId)
    {
        // Tear down every pad this machine had joined. Despawning the avatar
        // also despawns its sword (Player.OnNetworkDespawn).
        var dead = new List<(ulong, int)>();
        foreach (var key in pipes.Keys)
            if (key.clientId == clientId)
                dead.Add(key);

        foreach (var key in dead)
        {
            if (avatars.TryGetValue(key, out Player avatar) && avatar &&
                avatar.NetworkObject.IsSpawned)
                avatar.NetworkObject.Despawn();

            avatars.Remove(key);
            pipes.Remove(key);
        }
    }

    // ---------- client side ----------

    public static void RegisterPad(int padId)
    {
        using var writer = new FastBufferWriter(sizeof(int), Allocator.Temp);
        writer.WriteValueSafe(padId);

        NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(
            RegisterMsg, NetworkManager.ServerClientId, writer);
    }

    public static void SendInput(int padId, NetworkInputData data)
    {
        using var writer = new FastBufferWriter(64, Allocator.Temp);
        writer.WriteValueSafe(padId);
        writer.WriteNetworkSerializable(data);

        // Unreliable-sequenced: stale input is worthless, next frame corrects.
        NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(
            InputMsg, NetworkManager.ServerClientId, writer,
            NetworkDelivery.UnreliableSequenced);
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
