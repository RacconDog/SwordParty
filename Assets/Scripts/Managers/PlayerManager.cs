using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using System.Collections.Generic;

public class PlayerManager : MonoBehaviour
{
    public static PlayerManager instance;
    public static GameState curGameState { get; private set; } = GameState.NotPlaying;

    // Global freeze: while any upgrade picker is open, every player's
    // movement is paused and physics stops stepping (flying swords hang
    // mid-air). Counter so simultaneous pickers stack safely.
    public static bool MovementPaused => movementPauses > 0;
    private static int movementPauses;

    public static void PushMovementPause()
    {
        movementPauses++;
        if (movementPauses == 1)
            Physics.simulationMode = SimulationMode.Script;
    }

    public static void PopMovementPause()
    {
        movementPauses = Mathf.Max(0, movementPauses - 1);
        if (movementPauses == 0)
            Physics.simulationMode = SimulationMode.FixedUpdate;
    }

    [SerializeField] private PlayerInputManager playerInputManager;

    [Header("Debug")]
    [Tooltip("TEMP: while online, allow only one local pad to join on this " +
             "machine, so same-machine multi-instance testing doesn't bind " +
             "one pad to players in several instances.")]
    [SerializeField] private bool oneLocalPadOnline = true;

    [Header("Player Avatars")]
    [SerializeField] private Player playerPrefab;
    [SerializeField] private Transform playerSpawnPoint;

    public List<InputPipe> playerList = new List<InputPipe>();

    public enum GameState
    {
        OnlineHost,
        OnlineJoin,
        Local,
        NotPlaying
    }

    void Awake()
    {
        // Statics survive scene reloads; never start a session pre-paused
        // or with physics still halted from a mid-choice reset.
        movementPauses = 0;
        Physics.simulationMode = SimulationMode.FixedUpdate;

        // Set in Awake (not Start) so instance is ready before any spawned
        // controller's Start() tries to register.
        if (!instance)
        {
            instance = this;
        }
        else
        {
            Debug.LogWarning("more than one fuckin player managers yoo");
        }
    }

    void Start()
    {
        // Pads may join once we're in a session: hosting (local or online)
        // or connected to someone else's.
        NetworkManager.Singleton.OnServerStarted += HandleServerStarted;
        NetworkManager.Singleton.OnClientConnectedCallback += HandleClientConnected;

        playerInputManager.onPlayerJoined += HandleLocalPadJoined;
    }

    void OnDestroy()
    {
        if (NetworkManager.Singleton)
        {
            NetworkManager.Singleton.OnServerStarted -= HandleServerStarted;
            NetworkManager.Singleton.OnClientConnectedCallback -= HandleClientConnected;
        }

        if (playerInputManager)
            playerInputManager.onPlayerJoined -= HandleLocalPadJoined;
    }

    private void HandleLocalPadJoined(PlayerInput playerInput)
    {
        // TEMP same-machine testing guard: online, this machine takes exactly
        // one pad — close joining as soon as the first one lands.
        bool online = curGameState == GameState.OnlineHost ||
                      curGameState == GameState.OnlineJoin;

        if (oneLocalPadOnline && online)
            playerInputManager.enabled = false;
    }

    private void HandleServerStarted()
    {
        // Self-wire the input hub for this session, then open pad joining.
        NetworkController.HostSession();
        EnablePadJoining();
    }

    private void HandleClientConnected(ulong clientId)
    {
        // Fires on every machine for every join; we only care about our own
        // connection on a non-host machine (the host used OnServerStarted).
        if (!NetworkManager.Singleton.IsServer &&
            clientId == NetworkManager.Singleton.LocalClientId)
            EnablePadJoining();
    }

    private void EnablePadJoining()
    {
        // Each join spawns a LocalController for that pad, which piggybacks
        // into the simulation locally (host) or through the hub (client).
        playerInputManager.enabled = true;
    }

    public void BeginLocalSession()
    {
        ChangeGameState(GameState.Local);

        // Local play is just a host session nobody joins — one spawn path
        // for every mode. Reset the transport to direct UDP in case a
        // previous online session left relay data on it.
        NetworkManager.Singleton.GetComponent<UnityTransport>()
            .SetConnectionData("127.0.0.1", 7777);
        NetworkManager.Singleton.StartHost();
    }

    // Server-only: register a pipe, spawn its networked avatar, and mount.
    public Player HandlePlayerSpawn(InputPipe thisPlayer)
    {
        thisPlayer.playerID = playerList.Count;
        playerList.Add(thisPlayer);

        Vector3 pos = playerSpawnPoint ? playerSpawnPoint.position : Vector3.zero;
        Player player = Instantiate(playerPrefab, pos, Quaternion.identity);
        player.NetworkObject.Spawn();
        thisPlayer.MountToPlayer(player);

        return player;
    }

    public void ChangeGameState(GameState state)
    {
        curGameState = state;
    }
}
