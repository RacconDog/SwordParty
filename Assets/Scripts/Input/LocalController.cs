using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Netcode;

// The pad reader spawned per joined controller on EVERY machine. On the host
// it pipes straight into the simulation; on a client machine the exact same
// reader pipes through the NetworkController hub instead, which owns a
// host-side pipe for this pad.
public class LocalController : MonoBehaviour
{
    private PlayerInput playerInput;
    private InputData data = new InputData();
    private InputPipe pipe = new InputPipe();
    private InputAction confirmAction;

    private bool hostSide;
    private int padId;

    void Start()
    {
        playerInput = GetComponent<PlayerInput>();
        padId = playerInput.playerIndex;

        // FindAction is null-safe so the game still runs before the Confirm
        // action (buttonSouth) is added to the input actions asset.
        confirmAction = playerInput.actions.FindAction("Confirm");

        hostSide = NetworkManager.Singleton.IsServer;
        if (hostSide)
            PlayerManager.instance.HandlePlayerSpawn(pipe);
        else
            NetworkController.RegisterPad(padId);
    }

    void Update()
    {
        Vector2 move = playerInput.actions["Move"].ReadValue<Vector2>();
        Vector2 look = playerInput.actions["Look"].ReadValue<Vector2>();
        bool dash = playerInput.actions["Dash"].IsPressed();
        bool attack = playerInput.actions["Attack"].IsPressed();
        bool throwAttack = playerInput.actions["Throw"].IsPressed();
        bool confirm = confirmAction != null && confirmAction.IsPressed();

        if (hostSide)
        {
            if (!pipe.IsMounted) return;

            data.id = pipe.playerID;
            data.move = move;
            data.look = look;
            data.dash = dash;
            data.attack = attack;
            data.throwAttack = throwAttack;
            data.confirm = confirm;

            pipe.PushInput(data);
        }
        else
        {
            NetworkController.SendInput(padId, new NetworkInputData
            {
                move = move,
                look = look,
                dash = dash,
                attack = attack,
                throwAttack = throwAttack,
                confirm = confirm,
            });
        }
    }
}
