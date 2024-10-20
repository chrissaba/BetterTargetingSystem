using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using BetterTargetingSystem.Windows;
using System;
using System.Collections.Generic;
using System.Linq;


using Dalamud.Plugin.Services;

using DalamudCharacter = Dalamud.Game.ClientState.Objects.Types.ICharacter;
using DalamudGameObject = Dalamud.Game.ClientState.Objects.Types.IGameObject;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace BetterTargetingSystem;

public sealed unsafe class Plugin : IDalamudPlugin
{
    public string Name => "Better Targeting System";
    public string CommandConfig => "/bts";
    public string CommandHelp => "/btshelp";

    internal IEnumerable<uint> LastConeTargets { get; private set; } = Enumerable.Empty<uint>();
    internal List<uint> CyclingTargets { get; private set; } = new List<uint>();
    internal DebugMode DebugMode { get; private set; }

    private IDalamudPluginInterface PluginInterface { get; init; }
    private ICommandManager CommandManager { get; init; }
    private IFramework Framework { get; set; }
    private IPluginLog PluginLog { get; init; }
    public Configuration Configuration { get; init; }

    [PluginService] internal static IClientState Client { get; set; } = null!;
    [PluginService] private static IObjectTable ObjectTable { get; set; } = null!;
    [PluginService] private static ITargetManager TargetManager { get; set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; set; } = null!;
    [PluginService] private static IGameInteropProvider GameInteropProvider { get; set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; }

    private ConfigWindow ConfigWindow { get; init; }
    private HelpWindow HelpWindow { get; init; }
    private WindowSystem WindowSystem = new("BetterTargetingSystem");

    // Shamelessly stolen, not sure what that game function exactly does but it works
    [Signature("48 89 5C 24 ?? 57 48 83 EC 20 48 8B DA 8B F9 E8 ?? ?? ?? ?? 4C 8B C3")]
    internal static CanAttackDelegate? CanAttackFunction = null!;
    internal delegate nint CanAttackDelegate(nint a1, nint objectAddress);

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IPluginLog pluginLog,
        IFramework framework)
    {
        GameInteropProvider.InitializeFromAttributes(this);

        this.PluginInterface = pluginInterface;
        this.CommandManager = commandManager;
        this.PluginLog = pluginLog;

        this.Configuration = this.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.Configuration.Initialize(this.PluginInterface);

        Framework = framework;
        Framework.Update += Update;
        Client.TerritoryChanged += ClearLists;

        ConfigWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(ConfigWindow);
        HelpWindow = new HelpWindow(this);
        WindowSystem.AddWindow(HelpWindow);

        this.DebugMode = new DebugMode(this);
        this.PluginInterface.UiBuilder.Draw += DrawUI;
        this.PluginInterface.UiBuilder.OpenMainUi += DrawHelpUI;
        this.PluginInterface.UiBuilder.OpenConfigUi += DrawConfigUI;

        this.CommandManager.AddHandler(CommandConfig, new CommandInfo(ShowConfigWindow)
            { HelpMessage = "Open the configuration window." });
        this.CommandManager.AddHandler(CommandHelp, new CommandInfo(ShowHelpWindow)
            { HelpMessage = "What does this plugin do?" });
    }

    public void Dispose()
    {
        Framework.Update -= Update;
        Client.TerritoryChanged -= ClearLists;
        this.CommandManager.RemoveHandler(CommandConfig);
        this.CommandManager.RemoveHandler(CommandHelp);
        this.WindowSystem.RemoveAllWindows();
        ConfigWindow.Dispose();
        HelpWindow.Dispose();
    }

    public void Log(string message) => PluginLog.Debug(message);
    private void DrawUI() => this.WindowSystem.Draw();
    private void DrawHelpUI() => HelpWindow.Toggle();
    private void DrawConfigUI() => ConfigWindow.Toggle();
    private void ShowHelpWindow(string command, string args) => this.DrawHelpUI();
    private void ShowConfigWindow(string command, string args) => this.DrawConfigUI();

    public void ClearLists(ushort territoryType)
    {
        // Attempt to fix a very rare bug I can't reproduce
        this.LastConeTargets = new List<uint>();
        this.CyclingTargets = new List<uint>();
    }

    internal List<DalamudGameObject> GlobalPriorityTargets { get; private set; } = new List<DalamudGameObject>();

    public void Update(IFramework framework)
    {
        // Disable in GPose
        if (Client.IsGPosing)
            return;

        // Disable if keyboard is being used to type text
        if (Utils.IsTextInputActive || ImGuiNET.ImGui.GetIO().WantCaptureKeyboard)
            return;

        Keybinds.Keybind.GetKeyboardState();

        if (Configuration.TabTargetKeybind.IsPressed())
        {
            try { KeyState[(int)Configuration.TabTargetKeybind.Key!] = false; } catch { }
            CycleTargets();
            return;
        }

        if (Configuration.ClosestTargetKeybind.IsPressed())
        {
            try { KeyState[(int)Configuration.ClosestTargetKeybind.Key!] = false; } catch { }
            TargetClosest();
            return;
        }

        if (Configuration.LowestHealthTargetKeybind.IsPressed())
        {
            try { KeyState[(int)Configuration.LowestHealthTargetKeybind.Key!] = false; } catch { }
            TargetLowestHealth();
            return;
        }

        if (Configuration.BestAOETargetKeybind.IsPressed())
        {
            try { KeyState[(int)Configuration.BestAOETargetKeybind.Key!] = false; } catch { }
            TargetBestAOE();
            return;
        }
    }

    private void SetTarget(DalamudGameObject? target)
    {
        TargetManager.SoftTarget = null;
        TargetManager.Target = target;
    }

    private void TargetLowestHealth() => TargetClosest(true);

    private void TargetClosest(bool lowestHealth = false)
    {
        if (Client.LocalPlayer == null)
        {
            PluginLog.Debug($"Client.LocalPlayer is {Client.LocalPlayer}");
            return;
        }

        // Fetch targets from the global priority list first
        var (Targets, CloseTargets, EnemyListTargets, OnScreenTargets) = GetTargets();
        var priorityTargets = GlobalPriorityTargets.Count > 0 ? GlobalPriorityTargets : CloseTargets;

        // If no priority targets, fall back to normal logic
        var _targets = priorityTargets.Count > 0 ? priorityTargets
                      : OnScreenTargets.Count > 0 ? OnScreenTargets
                      : EnemyListTargets;

        if (_targets.Count == 0)
            return;

        var _target = lowestHealth
            ? _targets.OrderBy(o => (o as DalamudCharacter)?.CurrentHp)
                      .ThenBy(o => Utils.DistanceBetweenObjects(Client.LocalPlayer, o)).First()
            : _targets.OrderBy(o => Utils.DistanceBetweenObjects(Client.LocalPlayer, o)).First();

        SetTarget(_target);
    }

    public record ObjectsList(List<DalamudGameObject> Targets, List<DalamudGameObject> CloseTargets, List<DalamudGameObject> TargetsEnemy, List<DalamudGameObject> OnScreenTargets);


    private class AOETarget
    {
        public DalamudGameObject obj;
        public int inRange = 0;
        public AOETarget(DalamudGameObject obj) => this.obj = obj;
    }
    private void TargetBestAOE()
    {
        if (Client.LocalPlayer == null)
        {
            PluginLog.Debug($"Client.LocalPlayer is {Client.LocalPlayer}");
            return;
        }

        var (Targets, CloseTargets, EnemyListTargets, OnScreenTargets) = GetTargets();

        // Step 1: Prioritize Enemy List Targets
        var AOETargetsList = new List<AOETarget>();

        if (EnemyListTargets.Count > 0)
        {
            foreach (var enemy in EnemyListTargets)
            {
                var AOETarget = new AOETarget(enemy);
                foreach (var other in EnemyListTargets)
                {
                    if (other == enemy) continue;
                    if (Utils.DistanceBetweenObjects(enemy, other) <= 5) // Assuming 5 is the range
                    {
                        AOETarget.inRange += 1;
                    }
                }
                AOETargetsList.Add(AOETarget);
            }
        }

        // Check if we found any AOE targets in the Enemy List
        if (AOETargetsList.Count > 0)
        {
            var _target = AOETargetsList
                .OrderByDescending(o => o.inRange)
                .ThenByDescending(o => (o.obj as DalamudCharacter)?.CurrentHp)
                .FirstOrDefault()?.obj;

            if (_target != null)
            {
                SetTarget(_target);
                return; // Target found in the enemy list, no need to continue
            }
        }

        // Step 2: Check Targets in the Cone
        AOETargetsList.Clear(); // Clear previous list for new targets
        foreach (var target in Targets)
        {
            var AOETarget = new AOETarget(target);
            foreach (var other in Targets)
            {
                if (other == target) continue;
                if (Utils.DistanceBetweenObjects(target, other) <= 5) // Assuming 5 is the range
                {
                    AOETarget.inRange += 1;
                }
            }
            AOETargetsList.Add(AOETarget);
        }

        // Check if we found any AOE targets in the Cone
        if (AOETargetsList.Count > 0)
        {
            var _target = AOETargetsList
                .OrderByDescending(o => o.inRange)
                .ThenByDescending(o => (o.obj as DalamudCharacter)?.CurrentHp)
                .FirstOrDefault()?.obj;

            if (_target != null)
            {
                SetTarget(_target);
                return; // Target found in the cone, no need to continue
            }
        }

        // Step 3: Check On-Screen Targets
        AOETargetsList.Clear(); // Clear previous list for new targets
        foreach (var target in OnScreenTargets)
        {
            var AOETarget = new AOETarget(target);
            foreach (var other in OnScreenTargets)
            {
                if (other == target) continue;
                if (Utils.DistanceBetweenObjects(target, other) <= 5) // Assuming 5 is the range
                {
                    AOETarget.inRange += 1;
                }
            }
            AOETargetsList.Add(AOETarget);
        }

        // Final check if we found any AOE targets on screen
        if (AOETargetsList.Count > 0)
        {
            var _target = AOETargetsList
                .OrderByDescending(o => o.inRange)
                .ThenByDescending(o => (o.obj as DalamudCharacter)?.CurrentHp)
                .FirstOrDefault()?.obj;

            if (_target != null)
            {
                SetTarget(_target); // Set the target if found
                return;
            }
        }

        // If we reach here, no targets were found
        PluginLog.Debug("No valid AOE target found in any category.");
    }

    private void CycleTargets()
    {
        if (Client.LocalPlayer == null)
        {
            PluginLog.Debug($"Client.LocalPlayer is {Client.LocalPlayer}");
            return;
        }

        var (Targets, CloseTargets, EnemyListTargets, OnScreenTargets) = GetTargets();

        // Combine cone targets and close circle targets
        var AllTargets = Targets.Union(CloseTargets).ToList();

        // All objects in AllTargets are in OnScreenTargets so it's not necessary to test them
        if (EnemyListTargets.Count == 0 && OnScreenTargets.Count == 0 && AllTargets.Count == 0)
            return;

        var _currentTarget = TargetManager.Target;
        var _previousTarget = TargetManager.PreviousTarget;
        var _targetObjectId = _currentTarget?.EntityId ?? _previousTarget?.EntityId ?? 0;

        // Targets in the frontal cone or close circle
        if (AllTargets.Count > 0)
        {
            AllTargets = AllTargets.OrderBy(o => Utils.DistanceBetweenObjects(Client.LocalPlayer, o)).ToList();

            var AllTargetsObjectIds = AllTargets.Select(o => o.EntityId);

            // Same targets as last cycle
            if (this.LastConeTargets.ToHashSet().SetEquals(AllTargetsObjectIds.ToHashSet()))
            {
                // Add the close targets to the list of potential targets
                var _potentialTargets = AllTargets;
                var _potentialTargetsObjectIds = _potentialTargets.Select(o => o.EntityId);

                // New enemies to be added
                if (_potentialTargetsObjectIds.Any(o => this.CyclingTargets.Contains(o) == false))
                    this.CyclingTargets = this.CyclingTargets.Union(_potentialTargetsObjectIds).ToList();

                // We simply select the next target
                this.CyclingTargets = this.CyclingTargets.Intersect(_potentialTargetsObjectIds).ToList();
                var index = this.CyclingTargets.FindIndex(o => o == _targetObjectId);
                if (index == this.CyclingTargets.Count - 1) index = -1;
                SetTarget(_potentialTargets.Find(o => o.EntityId == this.CyclingTargets[index + 1]));
            }
            else
            {
                var _potentialTargets = AllTargets;
                var _potentialTargetsObjectIds = _potentialTargets.Select(o => o.EntityId).ToList();
                var index = _potentialTargetsObjectIds.FindIndex(o => o == _targetObjectId);
                if (index == _potentialTargetsObjectIds.Count - 1) index = -1;
                SetTarget(_potentialTargets.Find(o => o.EntityId == _potentialTargetsObjectIds[index + 1]));

                this.LastConeTargets = AllTargetsObjectIds;
                this.CyclingTargets = _potentialTargetsObjectIds;
            }

            return;
        }

        this.LastConeTargets = Enumerable.Empty<uint>();

        // Existing logic for CloseTargets, EnemyListTargets, and OnScreenTargets remains unchanged...
    }


    internal ObjectsList GetTargets()
    {
        /* Always return 4 lists.
         * The enemies in a cone in front of the player
         * The enemies in a close radius around the player
         * The enemies in the Enemy List Addon
         * All the targets on screen
         */
        var TargetsList = new List<DalamudGameObject>();
        var CloseTargetsList = new List<DalamudGameObject>();
        var TargetsEnemyList = new List<DalamudGameObject>();
        var OnScreenTargetsList = new List<DalamudGameObject>();

        var Player = Client.LocalPlayer != null ? (GameObject*)Client.LocalPlayer.Address : null;
        if (Player == null)
            return new ObjectsList(TargetsList, CloseTargetsList, TargetsEnemyList, OnScreenTargetsList);

        // There might be a way to store this and just update the values if they actually change
        var device = Device.Instance();
        float deviceWidth = device->Width;
        float deviceHeight = device->Height;

        var PotentialTargets = ObjectTable.Where(
            o => (ObjectKind.BattleNpc.Equals(o.ObjectKind)
                || ObjectKind.Player.Equals(o.ObjectKind))
            && o != Client.LocalPlayer
            && Utils.CanAttack(o)
        );

        var EnemyList = Utils.GetEnemyListObjectIds();

        foreach (var obj in PotentialTargets)
        {
            // In the enemy list addon, adding it to the Enemy list
            if (EnemyList.Contains(obj.EntityId))
                TargetsEnemyList.Add(obj);

            var o = (GameObject*)obj.Address;
            if (o == null) continue;

            if (o->GetIsTargetable() == false) continue;

            // If the object is part of another party's treasure hunt/leve, we ignore it
            if ((o->EventId.ContentId == EventHandlerType.TreasureHuntDirector || o->EventId.ContentId == EventHandlerType.BattleLeveDirector)
                && o->EventId.Id != Player->EventId.Id)
                continue;

            var distance = Utils.DistanceBetweenObjects(Client.LocalPlayer!, obj);

            // This is a bit less than the max distance to target something the vanilla way
            if (distance > 49) continue;

            /*
             * Check if object is visible on screen or not.
             * Using both WorldToScreenPoint and WorldToScreen because
             *  - the former is more accurate for actual X,Y position on screen
             *  - the latter returns whether or not the object is "in front" of the camera
             * This isn't exactly how I'd like to do it but since I couldn't find how to get
             * the "bounding box" of a game object or the dimensions of its model, this will have to do.
             */
            FFXIVClientStructs.FFXIV.Common.Math.Vector2 screenPos = new();
            FFXIVClientStructs.FFXIV.Common.Math.Vector3 worldPos = o->Position;
            FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Camera.WorldToScreenPoint(&screenPos, &worldPos);
            if (screenPos.X < 0
                || screenPos.X > deviceWidth
                || screenPos.Y < 0
                || screenPos.Y > deviceHeight) continue;
            if (GameGui.WorldToScreen(o->Position, out _) == false) continue;

            // Check actual line of sight from camera to object (blocked by walls, etc)
            if (Utils.IsInLineOfSight(o, true) == false) continue;

            // On screen and in light of sight of the camera, adding it to the On Screen list
            OnScreenTargetsList.Add(obj);

            // Close to the player, adding it to the Close targets list
            if (Configuration.CloseTargetsCircleEnabled && distance < Configuration.CloseTargetsCircleRadius)
                CloseTargetsList.Add(obj);

            // Further than the bigger cone, don't care about targeting it
            if (Configuration.Cone3Enabled)
            {
                if (distance > Configuration.Cone3Distance)
                    continue;
            }
            else if (Configuration.Cone2Enabled)
            {
                if (distance > Configuration.Cone2Distance)
                    continue;
            }
            else if (distance > Configuration.Cone1Distance)
                continue;

            // Default cone angle for very close targets, getting wider the closer the target is
            var angle = Configuration.Cone1Angle;
            if (Configuration.Cone3Enabled)
            {
                if (Configuration.Cone2Enabled)
                {
                    if (distance > Configuration.Cone2Distance)
                        angle = Configuration.Cone3Angle;
                    else if (distance > Configuration.Cone1Distance)
                        angle = Configuration.Cone2Angle;
                }
                else if (distance > Configuration.Cone1Distance)
                    angle = Configuration.Cone3Angle;
            }
            else if (Configuration.Cone2Enabled && distance > Configuration.Cone1Distance)
                angle = Configuration.Cone2Angle;

            if (Utils.IsInFrontOfCamera(obj, angle) == false) continue;

            // In front of the player, adding it to the default list
            TargetsList.Add(obj);
        }

        // Update global priority list with cone targets
        GlobalPriorityTargets = TargetsList;

        return new ObjectsList(TargetsList, CloseTargetsList, TargetsEnemyList, OnScreenTargetsList);
    }
}
