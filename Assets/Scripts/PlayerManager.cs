using UnityEngine;
using Unity.Netcode;
using System.Collections.Generic;

public class PlayerManager : MonoBehaviour
{
    public static PlayerManager instance;
    public static GameState curGameState { get; private set; } = GameState.NotPlaying;

    public List<ControllerBase> playerList = new List<ControllerBase>();
    
    public enum GameState
    {
        OnlineHost,
        OnlineJoin,
        Local,
        NotPlaying
    }

    public void HandlePlayerSpawn(ControllerBase thisPlayer)
    {
        thisPlayer.playerID = playerList.Count;
        playerList.Add(thisPlayer);
    }

    void Start()
    {
        if(!instance)
        {
            instance = this;
        }
        else
        {
            Debug.LogWarning("more than one fuckin player managers yoo");
        }
    }

    public void ChangeGameState(GameState state)
    {
        curGameState = state;
    }
}
