using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Network;
using Dalamud.Logging;
using ImGuiNET;
using static NoClippy.NoClippy;

namespace NoClippy
{
    public partial class Configuration
    {
        public bool EnableAnimLockComp = true;
        public bool EnableLogging = false;
        public bool EnableDryRun = false;
        public Dictionary<uint, float> AnimationLocks = new();
    }
}

namespace NoClippy.Modules
{
    public class AnimationLock : Module
    {
        // ALL INFO BELOW IS BASED ON MY FINDINGS AND I RESERVE THE RIGHT TO HAVE MISINTERPRETED SOMETHING, THANKS
        // The typical time range that passes for the client is never equal to ping, it always seems to be at least ping + server delay
        // The server delay is usually around 40-60 ms in the overworld, but falls to 30-40 ms inside of instances
        // Additionally, your FPS will add more time because one frame MUST pass for you to receive the new animation lock
        // Therefore, most players will never receive a response within 40 ms at any ping
        // Another interesting fact is that the delay from the server will spike if you send multiple packets at the same time
        // This seems to imply that the server will not process more than one packet from you per tick
        // You can see this if you sheathe your weapon before using an action, you will notice delays that are around 50 ms higher than usual
        // This explains the phenomenon where moving seems to make it harder to weave

        // For these reasons, I do not believe it is possible to triple weave on any ping without clipping even the slightest amount as that would require 25 ms response times for a 2.5 GCD triple

        // This module simulates around 10 ms ping inside instances

        public override bool IsEnabled
        {
            get => Config.EnableAnimLockComp;
            set => Config.EnableAnimLockComp = value;
        }

        public override int DrawOrder => 1;

        private const float simulatedRTT = 0.04f;
        private float delay = -1;
        private int packetsSent = 0;
        private bool isCasting = false;
        private float intervalPacketsTimer = 0;
        private int intervalPacketsIndex = 0;
        private readonly int[] intervalPackets = new int[5]; // Record the last 50 ms of packets
        private bool enableAnticheat = false;

        public bool IsDryRunEnabled => enableAnticheat || Config.EnableDryRun;

        private float AverageDelay(float currentDelay, float weight) =>
            delay > 0
                ? delay = delay * (1 - weight) + currentDelay * weight
                : delay = currentDelay; // Initial starting delay

        private static float GetAnimationLock(uint actionID) => (!Config.AnimationLocks.TryGetValue(actionID, out var animationLock) || animationLock < 0.5f
                ? Game.DefaultClientAnimationLock
                : animationLock)
            + simulatedRTT;

        private static void UpdateDatabase(uint actionID, float animationLock)
        {
            if (Config.AnimationLocks.TryGetValue(actionID, out var oldLock) && oldLock == animationLock) return;
            Config.AnimationLocks[actionID] = animationLock;
            Config.Save();
            PluginLog.Debug($"Recorded new animation lock value of {F2MS(animationLock)} ms for {actionID}");
        }

        private unsafe void UseActionLocation(IntPtr actionManager, uint actionType, uint actionID, long targetedActorID, IntPtr vectorLocation, uint param)
        {
            packetsSent = intervalPackets.Sum();

            if (Game.actionManager->animationLock != Game.DefaultClientAnimationLock) return;

            var id = Game.GetSpellIDForAction(actionType, actionID);
            var animationLock = GetAnimationLock(id);
            if (!IsDryRunEnabled)
                Game.actionManager->animationLock = animationLock;

            PluginLog.Debug($"Applying {F2MS(animationLock)} ms animation lock for {actionType} {actionID} ({id})");
        }

        private void CastBegin(ulong objectID, IntPtr packetData) => isCasting = true;
        private void CastInterrupt(IntPtr actionManager, uint actionType, uint actionID) => isCasting = false;

        private unsafe void ReceiveActionEffect(int sourceActorID, IntPtr sourceActor, IntPtr vectorPosition, IntPtr effectHeader, IntPtr effectArray, IntPtr effectTrail, float oldLock, float newLock)
        {
            try
            {
                if (oldLock == newLock || sourceActor != DalamudApi.ClientState.LocalPlayer?.Address) return;

                // Ignore cast locks (caster tax, teleport, lb)
                if (isCasting)
                {
                    isCasting = false;

                    // The old lock should always be 0, but high ping can cause the packet to arrive too late and allow near instant double weaves
                    newLock += oldLock;
                    if (!IsDryRunEnabled)
                        Game.actionManager->animationLock = newLock;

                    if (Config.EnableLogging)
                        PrintLog($"Cast Lock: {F2MS(newLock)} ms (+{F2MS(oldLock)})");
                    return;
                }

                if (newLock != *(float*)(effectHeader + 0x10))
                {
                    PrintError("Mismatched animation lock offset!");
                    return;
                }

                // Special case to (mostly) prevent accidentally using XivAlexander at the same time
                var isUsingAlexander = newLock % 0.01 is >= 0.0005f and <= 0.0095f;
                if (!enableAnticheat && isUsingAlexander)
                {
                    enableAnticheat = true;
                    PrintError($"Unexpected lock of {F2MS(newLock)} ms, temporary dry run has been enabled");
                }

                var actionID = *(uint*)(effectHeader + 0x8);
                var appliedLock = GetAnimationLock(actionID);
                var lastRecordedLock = appliedLock - simulatedRTT;

                if (!enableAnticheat)
                    UpdateDatabase(actionID, newLock);

                // Get the difference between the recorded animation lock and the real one
                var correction = newLock - lastRecordedLock;
                var rtt = appliedLock - oldLock;

                if (rtt <= simulatedRTT)
                {
                    if (Config.EnableLogging)
                        PrintLog($"RTT ({F2MS(rtt)} ms) was lower than {F2MS(simulatedRTT)} ms, no adjustments were made");
                    return;
                }

                var prevAverage = delay;
                var newAverage = AverageDelay(rtt, packetsSent > 1 ? 0.1f : 1f);
                var average = Math.Max(prevAverage > 0 ? prevAverage : newAverage, 0.001f);

                var variationMultiplier = Math.Max(rtt / average, 1) - 1;
                var networkVariation = simulatedRTT * variationMultiplier;

                var adjustedAnimationLock = Math.Max(oldLock + correction + networkVariation, 0);

                if (!IsDryRunEnabled && float.IsFinite(adjustedAnimationLock) && adjustedAnimationLock < 10)
                    Game.actionManager->animationLock = adjustedAnimationLock;

                if (!Config.EnableLogging) return;

                var logString = IsDryRunEnabled ? "[DRY] " : string.Empty;
                logString += $"Action: {actionID} {(correction > 0 ? $"({F2MS(lastRecordedLock)} > {F2MS(newLock)} ms)" : $"({F2MS(newLock)} ms)")}";
                logString += $" || RTT: {F2MS(rtt)} (+{variationMultiplier:P0}) ms";

                if (enableAnticheat)
                    logString += $" [Alexander: {F2MS(rtt - (lastRecordedLock - newLock))} ms]";

                logString += $" || Lock: {F2MS(oldLock)} > {F2MS(adjustedAnimationLock)} ({F2MS(correction + networkVariation):+0;-#}) ms";
                logString += $" || Packets: {packetsSent}";

                PrintLog(logString);
            }
            catch { PrintError("Error in AnimationLock Module"); }
        }

        private void NetworkMessage(IntPtr dataPtr, ushort opCode, uint sourceActorId, uint targetActorId, NetworkMessageDirection direction)
        {
            if (direction != NetworkMessageDirection.ZoneUp) return;
            intervalPackets[intervalPacketsIndex]++;
        }

        private void Update()
        {
            intervalPacketsTimer += (float)DalamudApi.Framework.UpdateDelta.TotalSeconds;
            while (intervalPacketsTimer >= 0.01f)
            {
                intervalPacketsTimer -= 0.01f;
                intervalPacketsIndex = (intervalPacketsIndex + 1) % intervalPackets.Length;
                intervalPackets[intervalPacketsIndex] = 0;
            }
        }

        public override void DrawConfig()
        {
            if (ImGui.Checkbox("Enable Animation Lock Reduction", ref Config.EnableAnimLockComp))
                Config.Save();
            PluginUI.SetItemTooltip("Modifies the way the game handles animation lock," +
                "\ncausing it to simulate 10 ms ping.");

            if (Config.EnableAnimLockComp)
            {
                ImGui.Columns(2, null, false);

                if (ImGui.Checkbox("Enable Logging", ref Config.EnableLogging))
                    Config.Save();

                ImGui.NextColumn();

                var _ = IsDryRunEnabled;
                if (ImGui.Checkbox("Dry Run", ref _))
                {
                    Config.EnableDryRun = _;
                    enableAnticheat = false;
                    Config.Save();
                }
                PluginUI.SetItemTooltip("The plugin will still log and perform calculations, but no in-game values will be overwritten.");
            }

            ImGui.Columns(1);
        }

        public override void Enable()
        {
            Game.OnUseActionLocation += UseActionLocation;
            Game.OnCastBegin += CastBegin;
            Game.OnCastInterrupt += CastInterrupt;
            Game.OnReceiveActionEffect += ReceiveActionEffect;
            Game.OnNetworkMessage += NetworkMessage;
            Game.OnUpdate += Update;
        }

        public override void Disable()
        {
            Game.OnUseActionLocation -= UseActionLocation;
            Game.OnCastBegin -= CastBegin;
            Game.OnCastInterrupt -= CastInterrupt;
            Game.OnReceiveActionEffect -= ReceiveActionEffect;
            Game.OnNetworkMessage -= NetworkMessage;
            Game.OnUpdate -= Update;
        }
    }
}
