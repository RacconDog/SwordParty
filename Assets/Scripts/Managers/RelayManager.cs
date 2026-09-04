using System;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;
using Unity.Networking;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
// using Console = DeveloperConsole.Console;

public class RelayManager : MonoBehaviour
{
    private async void Start()
    {
        // Services + auth live outside the scene, so a scene reload reruns
        // this Start() while we're still initialized/signed in from before.
        if (UnityServices.State == ServicesInitializationState.Uninitialized)
            await UnityServices.InitializeAsync();

        if (AuthenticationService.Instance.IsSignedIn)
            return;

        AuthenticationService.Instance.SignedIn += () =>
        {
            print("Youve been Signed in as: " + AuthenticationService.Instance.PlayerId);
        };
        await AuthenticationService.Instance.SignInAnonymouslyAsync();
    }

    public async void CreateRelay()
    {
        try
        {
            Allocation allocation = await RelayService.Instance.CreateAllocationAsync(3);

            string joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
            print("Lobby Code: " + joinCode);

            // Auto-copy so the host can paste the code straight to friends.
            GUIUtility.systemCopyBuffer = joinCode;
        
        
            NetworkManager.Singleton.GetComponent<UnityTransport>().SetHostRelayData(
                allocation.RelayServer.IpV4,
                (ushort)allocation.RelayServer.Port,
                allocation.AllocationIdBytes,
                allocation.Key,
                allocation.ConnectionData
            );

            NetworkManager.Singleton.StartHost();
        }
        catch (RelayServiceException e)
        {
            print(e.ToString());
        }
    }

    public async void JoinRelay(string joinCode)
    {
        try
        {
            print("Joining relay " + joinCode);
            JoinAllocation joinAllocation = await RelayService.Instance.JoinAllocationAsync(joinCode);

            NetworkManager.Singleton.GetComponent<UnityTransport>().SetClientRelayData
            (
                joinAllocation.RelayServer.IpV4,
                (ushort)joinAllocation.RelayServer.Port,
                joinAllocation.AllocationIdBytes,
                joinAllocation.Key,
                joinAllocation.ConnectionData,
                joinAllocation.HostConnectionData
            );

            NetworkManager.Singleton.StartClient();
        }
        catch (RelayServiceException e)
        {
            print(e.ToString());
        }
    }
}
