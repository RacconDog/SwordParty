using UnityEngine;
using UnityEngine.InputSystem;

public class LocalController : ControllerBase
{
    private PlayerInput playerInput;
    private InputData data = new InputData();

    protected override void OnStart()
    {
        playerInput = GetComponent<PlayerInput>();
    }

    void Update()
    {
        if (!mountedPlayer) return;

        data.id = playerID;
        data.move = playerInput.actions["Move"].ReadValue<Vector2>();
        data.look = playerInput.actions["Look"].ReadValue<Vector2>();
        data.dash = playerInput.actions["Dash"].IsPressed();
        data.attack = playerInput.actions["Attack"].IsPressed();
        data.throwAttack = playerInput.actions["Throw"].IsPressed();

        PushInput(data);
    }
}
