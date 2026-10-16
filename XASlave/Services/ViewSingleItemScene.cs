using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Threading;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using CharacterBase = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase;
using Human = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Human;
using RenderCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Camera;
using SceneCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Camera;
using SceneCameraManager = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager;

namespace XASlave.Services;

// A value-only appearance and a temporary lease on one of the eight native UI
// render slots. Every native request keeps priority over this optional preview.
internal sealed unsafe partial class ViewSingleItemScene : IDisposable
{
    private const uint FirstObject = 0xF0, SlotCount = 8;
    private const int SceneSlotsOffset = 0xA0, ContextCountsOffset = 0x3835C;
    private readonly object sync = new();
    private readonly int[] foreignOperations = new int[SlotCount];
    private readonly nint moduleBase;
    private readonly Action retireFilter;
    private readonly List<(int Rva, byte[] Bytes)> proofs = new();
    private readonly List<(int Rva, byte[] Bytes)> resumeProofs = new();
    private readonly List<IDalamudHook> hooks = new();
    private readonly List<(Action Enable, int Rva, int Length)> hookProofs = new();
    private readonly Hook<InitializeDelegate> initializeHook;
    private readonly Hook<CreateDelegate> createHook;
    private readonly Hook<DeleteDelegate> deleteHook;
    private readonly Hook<ContextInitializeDelegate> contextHook;
    private readonly Hook<ContextInitializeSimpleDelegate> simpleContextHook;
    private readonly Hook<CameraAssignmentDelegate> cameraHook;
    private readonly PauseCharacterDelegate pauseCharacter;
    private readonly PauseCharacterDelegate resumeCharacter;
    private readonly DrawWeaponDelegate drawWeapon;
    private readonly SetupModelDelegate setupModel;
    private readonly CloneDrawDelegate disableDraw;
    private readonly UpdateItemVisibilityDelegate bodyVisibility, handsVisibility, legsVisibility, feetVisibility,
        earringsVisibility, necklaceVisibility, braceletVisibility, rightRingVisibility, leftRingVisibility, hairVisibility;
    private CharaViewModelData appearance;
    private CharaViewModelData leaseAppearance;
    private int itemSelection = -1, weaponSelection = -1;
    private bool selectionReady, pairedWeapon, weaponDrawn = true;
    private bool accessorySelection, accessoryProjected;
    private ViewSingleItemOrnament? fashionAccessory;
    private ViewSingleItemCompanion? previewCompanion;
    private CharacterBase.ModelType accessoryModelType;
    private CharaView* view;
    private nint character;
    private uint slot;
    private bool captured, preempted, disposed, constructed, cleanupBlocked;
    private int ownThread, ownOperation;
    private long retryAfter, leaseStarted;
    private float yaw, pitch, roll, zoom = 1, panX, panY, baseHeight = 5;
    private Vector2 viewport = new(640, 440);
    private long stillReadyAt;
    private ulong stillGeometry, pausedGeometry;
    private bool animationPaused, ownsPauseReason;
    private long weaponStateRetryAt;
    private int weaponStateAttempts;
    private string animationDiagnostic = "animation=not-ready";
    private nint fullGeometryModel, fullGeometryResource;
    private uint fullGeometryAttributes, fullGeometryShapes;
    private bool fullGeometryVisible;

    private delegate void InitializeDelegate(CharaView* self, AgentInterface* agent, uint index, nint callback);
    private delegate uint CreateDelegate(ClientObjectManager* manager, uint index, byte parameter);
    private delegate void DeleteDelegate(ClientObjectManager* manager, ushort index, byte parameter);
    private delegate byte ContextInitializeDelegate(nint context, uint index, uint cameraType);
    private delegate byte ContextInitializeSimpleDelegate(nint context, uint index);
    private delegate void CameraAssignmentDelegate(OffscreenRenderingManager* manager, uint index, RenderCamera* camera);
    private delegate void PauseCharacterDelegate(nint container, byte reason, ushort action);
    private delegate void DrawWeaponDelegate(nint timeline, byte drawn, byte playAnimation);
    private delegate void SetupModelDelegate(nint setup, uint modelCharaId, nint customize, nint equipment, nint glasses, float scale);
    private delegate void CloneDrawDelegate(nint clone);
    private delegate void UpdateItemVisibilityDelegate(Human* human, ulong* equipmentFlags);

    internal uint SlotIndex { get { lock (sync) return slot; } }
    internal object SyncRoot => sync;
    internal bool WeaponDrawn => weaponDrawn;
    internal bool IsFashionAccessory => accessorySelection && fashionAccessory is { Id: > 0, ModelCharaId: > 0 };
    internal bool IsStandaloneModel => IsFashionAccessory
        || ((itemSelection is 103 or 104) && previewCompanion is { Id: > 0, ModelCharaId: > 0 });
    private uint StandaloneModelId => IsFashionAccessory ? fashionAccessory!.ModelCharaId : previewCompanion?.ModelCharaId ?? 0;
    private string StandaloneModelName => itemSelection == 103 ? "minion" : itemSelection == 104 ? "mount" : "fashion accessory";
    internal string StatusText { get; private set; } = "Waiting for a standalone preview slot...";
    internal string ReadinessDiagnostic { get; private set; } = "stage=no-scene";
    internal string CameraDiagnostic => $"yaw={yaw:F3}; pitch={pitch:F3}; roll={roll:F3}; zoom={zoom:F3}; pan={panX:F3},{panY:F3}; viewport={viewport.X:F0}x{viewport.Y:F0}; itemSelection={itemSelection}; weaponSelection={weaponSelection}; paired={pairedWeapon}; requestedDrawn={DesiredWeaponDrawn}; accessory={fashionAccessory?.Id ?? 0}; companion={previewCompanion?.Id ?? 0}; standaloneModel={StandaloneModelId}; accessoryProjected={accessoryProjected}; {animationDiagnostic}";

    internal ViewSingleItemScene(nint moduleBase, PEReader disk, Action retireFilter)
    {
        NearbyZoneNativeBinding.RequireFramework();
        this.moduleBase = moduleBase;
        this.retireFilter = retireFilter;
        ValidateLayouts();
        using var registration = ViewSingleItemRendererHookState.GetSyncRoot().EnterScope();
        try
        {
            AddProof(disk, 0x9CDB70, 0x14D); // CharaView constructor
            AddProof(disk, 0x9DA860, 0x2B); // Destructor does not Release
            AddProof(disk, 0x9CDDD0, 0xDB);
            AddProof(disk, 0x9CDD60, 0x6A);
            AddProof(disk, 0x9CDEC0, 0x331);
            AddProof(disk, 0x9CE820, 0x2BC); // Initial appearance loads all three weapon values
            AddProof(disk, 0x9CD910, 0x253);
            AddProof(disk, 0x9CF260, 0x119);
            AddProof(disk, 0x8BFBC0, 0x1EC); // Same timeline setter used by CharaView; admits offhand-only clones
            AddProof(disk, 0x614E30, 0x9); // Owned emote predicate
            AddProof(disk, 0x614E70, 0x15); // Owned emote mode predicate
            AddProof(disk, 0x6145A0, 0xA2); // Owned emote cancellation before weapon animation
            AddProof(disk, 0x8BE980, 0x3D4); // Timeline action dispatch; linked SEPack branch is excluded
            AddProof(disk, 0x8D0C50, 0x635); // ModelChara setup, also used by native ornaments with null appearance arguments
            AddProof(disk, 0x8D1288, 0x20); // ModelChara setup switch table
            AddProof(disk, 0x898750, 0x10); // Setup's gameplay branch excludes owned UI object indices
            AddProof(disk, 0x89DF00, 0x1BF); // ObjectIndex stores the manager's index plus 0xC8
            AddProof(disk, 0x906920, 0x648); // Native model-ready creates the replacement draw through EnableDraw
            AddProof(disk, 0x906F70, 0x79); // Disable only this owned clone's initial human root
            AddProof(disk, 0x8990B0, 0x37);
            AddProof(disk, 0x899110, 0x7B);
            AddProof(disk, 0x899060, 0x3B);
            AddProof(disk, 0x8D38B0, 0x2D8); // Native standalone Human/DemiHuman/Monster model allocation
            AddProof(disk, 0x8D4500, 0xC); // Effective model row; secondary override is excluded
            AddProof(disk, 0x8D4520, 0xB); // Effective skeleton; secondary override is excluded
            AddProof(disk, 0x907170, 0x13); // BattleChara attaches only its own newly created draw
            AddProof(disk, 0x907570, 0x6A); // ModelChara setup's native scale/height virtual
            AddProof(disk, 0x8D4960, 0xBA);
            AddProof(disk, 0x8D4B60, 0x62);
            AddProof(disk, 0x8C9A30, 0x143); // Effects height uses the confirmed clone owner
            AddProof(disk, 0x8DBEC0, 0xB); // Effects height predicate reads the clone's mode only
            AddProof(disk, 0x9CF410, 0xB9); // Native model-ready binding after ordinary State3 progression
            AddProof(disk, 0x9CE200, 0x179); // Base ResetPositions uses camera and GameObject transforms only
            AddProof(disk, 0x9DC010, 0x3); // Base model-ready callback has no Human-specific work
            AddProof(disk, 0x9DBC60, 0x3); // Base completed-model callback has no Human-specific work
            AddProof(disk, 0x9DCCF0, 0x3); // Base per-frame callback has no Human-specific work
            AddProof(disk, 0x9CF380, 0xF);
            AddProof(disk, 0x1759090, 0x4D);
            AddProof(disk, 0x90D40, 0x20); // UI memory space
            AddProof(disk, 0x8F270, 0x69); // Matching native Free
            AddProof(disk, 0x41FDF1, 0x4E); // Manual aspect survives camera update
            AddProof(disk, 0x41FBC0, 0x27F); // LookAt camera path uses our rotated up vector
            AddProof(disk, 0x3390F0, 0x1F0); // LookAt basis, including camera roll
            AddProof(disk, 0x3382A0, 0x9E); // Camera up-vector quaternion transform
            AddProof(disk, 0x343A50, 0x132);
            AddProof(disk, 0x343B90, 0x130); // Quaternion multiplication helpers
            AddProof(disk, 0x446C50, 0x3AB); // Body visibility, retaining intrinsic IMC attributes
            AddProof(disk, 0x447010, 0x14C); // Hands
            AddProof(disk, 0x447170, 0x2EB); // Legs
            AddProof(disk, 0x447470, 0x1DD); // Feet
            AddProof(disk, 0x447660, 0x160); // Earrings
            AddProof(disk, 0x4477D0, 0xCC); // Necklace
            AddProof(disk, 0x4478B0, 0xA3); // Bracelet
            AddProof(disk, 0x447960, 0x7E); // Right ring
            AddProof(disk, 0x4479F0, 0x7E); // Left ring
            AddProof(disk, 0x447A80, 0x4B3); // Hair with native race/customize rules
            AddProof(disk, 0x447F34, 0x18); // Hair visibility jump table
            AddProof(disk, 0x449070, 0x4F); // Race classifier used by accessories and hair
            AddProof(disk, 0x4490C0, 0x1C); // Classifier jump table
            AddProof(disk, 0x2747A0, 0x43); // Read-only named attribute lookup
            AddProof(disk, 0x274860, 0x43); // Read-only named shape lookup
            AddProof(disk, 0x2E6D30, 0x98); // Lookup's resource string-map search
            AddProof(disk, 0x22D75B8, 8); // Native neutral equipment parameter default
            AddProof(disk, 0x2610E0, 0x1C2); // Orthographic width = height * aspect
            AddProof(disk, 0x269220, 0x1E); // Owned renderer-context constructor
            AddProof(disk, 0x2693B0, 0x80); // Renderer-context slot-count release
            AddProof(disk, 0x3346B0, 0x37); // Context reference release dispatch
            AddProof(disk, 0x269250, 0x7); // Context cleanup dispatch
            AddProof(disk, 0x2EE090, 0x4D); // Renderer-context destructor
            AddProof(disk, 0x41E9E0, 0x33); // Scene camera releases its own RenderCamera
            AddProof(disk, 0x8D70D0, 0x31C); // Character-only native pause, with reason 0x10
            AddAnimationSamplingProofs(disk);
            AddResumeProof(disk, 0x8D7400, 0x345); // Remove only our native pause reason before clone deletion
            AddResumeProof(disk, 0x470540, 0xC); // Resume the clone's external per-index physics
            AddResumeProof(disk, 0x6072B0, 0x17);
            AddProof(disk, 0x909C60, 0xF); // UI clone setup admits that native pause reason
            AddProof(disk, 0x607720, 0x32); // Pause owned scheduler slots
            AddProof(disk, 0x607760, 0x32); // Alternate UI scheduler slots
            AddProof(disk, 0x18CAF70, 0x60);
            AddProof(disk, 0x18CAFE0, 0xC8); // Pause the clone's weapon attachments
            AddProof(disk, 0x6072D0, 0x14);
            AddProof(disk, 0x19920C0, 0x84);
            AddResumeProof(disk, 0x8DBE00, 0x8); // Clone draw-object virtual getter
            AddProof(disk, 0x907490, 0x81); // Clone loaded-model virtual predicate
            AddProof(disk, 0x8DC0A0, 0x3); // Clone native pause notification
            AddResumeProof(disk, 0x8DC050, 0x3); // Clone native resume notification
            AddVtableProof(disk, 0x21A0D38, 0x68); // The base virtual table, including Release/Dtor
            AddVtableProof(disk, 0x21A4A98, 0x2C0); // ClientObjectManager's owned BattleChara
            resumeProofs.Add(proofs[^1]); // Cleanup also verifies every used virtual seam
            AddVtableProof(disk, 0x216C3D0, 0x48); // Scene.Camera virtual UpdateRender
            AddVtableProof(disk, 0x214CDE8, 0x30); // Context reference/cleanup/destructor dispatch
            // These name-pointer tables need the same ASLR normalization as
            // virtual tables. Native selected-slot calculations read them only.
            AddVtableProof(disk, 0x28A7B40, 0x50); // Body IMC attribute names
            AddVtableProof(disk, 0x28A7B90, 0x50); // Hands
            AddVtableProof(disk, 0x28A7BE0, 0x50); // Legs
            AddVtableProof(disk, 0x28A7C30, 0x50); // Feet
            initializeHook = AddHook<InitializeDelegate>(disk, 0x9CDCC0, 0x87, InitializeDetour, 10);
            createHook = AddHook<CreateDelegate>(disk, 0x1758940, 0x430, CreateDetour);
            deleteHook = AddHook<DeleteDelegate>(disk, 0x1758D70, 0x14C, DeleteDetour);
            contextHook = AddHook<ContextInitializeDelegate>(disk, 0x269320, 0x7D, ContextDetour, 10);
            simpleContextHook = AddHook<ContextInitializeSimpleDelegate>(disk, 0x269260, 0xBA, SimpleContextDetour, 10);
            cameraHook = AddHook<CameraAssignmentDelegate>(disk, 0x26ABC0, 0x62, CameraAssignmentDetour, 10);
            RequirePointer((nint)CharaView.MemberFunctionPointers.Ctor, 0x9CDB70);
            RequirePointer((nint)CharaView.MemberFunctionPointers.SetModelData, 0x9CDD60);
            RequirePointer((nint)CharaView.MemberFunctionPointers.Render, 0x9CDEC0);
            RequirePointer((nint)CharaView.MemberFunctionPointers.GetCharacter, 0x9CF380);
            RequirePointer((nint)CharaView.MemberFunctionPointers.ToggleDrawWeapon, 0x9CF260);
            RequirePointer((nint)CharaViewModelData.MemberFunctionPointers.CopyFromCharacter, 0x9CD910);
            RequirePointer((nint)ClientObjectManager.MemberFunctionPointers.GetObjectByIndex, 0x1759090);
            RequirePointer((nint)IMemorySpace.MemberFunctionPointers.GetUISpace, 0x90D40);
            RequirePointer((nint)IMemorySpace.MemberFunctionPointers.Free, 0x8F270);
            pauseCharacter = Marshal.GetDelegateForFunctionPointer<PauseCharacterDelegate>(moduleBase + 0x8D70D0);
            resumeCharacter = Marshal.GetDelegateForFunctionPointer<PauseCharacterDelegate>(moduleBase + 0x8D7400);
            drawWeapon = Marshal.GetDelegateForFunctionPointer<DrawWeaponDelegate>(moduleBase + 0x8BFBC0);
            setupModel = Marshal.GetDelegateForFunctionPointer<SetupModelDelegate>(moduleBase + 0x8D0C50);
            disableDraw = Marshal.GetDelegateForFunctionPointer<CloneDrawDelegate>(moduleBase + 0x906F70);
            bodyVisibility = Marshal.GetDelegateForFunctionPointer<UpdateItemVisibilityDelegate>(moduleBase + 0x446C50);
            handsVisibility = Marshal.GetDelegateForFunctionPointer<UpdateItemVisibilityDelegate>(moduleBase + 0x447010);
            legsVisibility = Marshal.GetDelegateForFunctionPointer<UpdateItemVisibilityDelegate>(moduleBase + 0x447170);
            feetVisibility = Marshal.GetDelegateForFunctionPointer<UpdateItemVisibilityDelegate>(moduleBase + 0x447470);
            earringsVisibility = Marshal.GetDelegateForFunctionPointer<UpdateItemVisibilityDelegate>(moduleBase + 0x447660);
            necklaceVisibility = Marshal.GetDelegateForFunctionPointer<UpdateItemVisibilityDelegate>(moduleBase + 0x4477D0);
            braceletVisibility = Marshal.GetDelegateForFunctionPointer<UpdateItemVisibilityDelegate>(moduleBase + 0x4478B0);
            rightRingVisibility = Marshal.GetDelegateForFunctionPointer<UpdateItemVisibilityDelegate>(moduleBase + 0x447960);
            leftRingVisibility = Marshal.GetDelegateForFunctionPointer<UpdateItemVisibilityDelegate>(moduleBase + 0x4479F0);
            hairVisibility = Marshal.GetDelegateForFunctionPointer<UpdateItemVisibilityDelegate>(moduleBase + 0x447A80);
            // Assign every delegate's hook field before enabling any detour.
            foreach (var target in hookProofs)
            {
                target.Enable();
                proofs.Add((target.Rva, NearbyZoneNativeBinding.Read(moduleBase + target.Rva, target.Length)));
            }
        }
        catch
        {
            foreach (var hook in hooks.AsEnumerable().Reverse()) hook.Dispose();
            throw;
        }
    }

    private static void ValidateLayouts()
    {
        ValidateAnimationLayouts();
        if (sizeof(CharaView) != 0x318 || sizeof(CharaViewModelData) != 0x98
            || sizeof(ClientObjectManager) != 0xFA0 || sizeof(ClientObjectManager.BattleCharaEntry) != 0x10
            || sizeof(SceneCameraManager) != 0x130 || sizeof(OffscreenRenderingManager) != 0x190
            || sizeof(SceneCamera) != 0x100 || sizeof(RenderCamera) != 0x290 || sizeof(Manager) != 0x38390
            || sizeof(Human) != 0xCD0 || sizeof(NativeCharacter) != 0x2370 || sizeof(CharacterBase) != 0xA20
            || sizeof(Model) != 0x180 || sizeof(ModelResourceHandle) != 0x2A0 || sizeof(ModelRenderer.Callback) != 0x20
            || sizeof(Companion) != 0x2470 || sizeof(CompanionContainer) != 0x20 || sizeof(MountContainer) != 0x68)
            throw new InvalidOperationException("The standalone preview layout changed.");
        RequireOffset<ClientObjectManager>("_battleCharas", 0x10);
        RequireOffset<ClientObjectManager.BattleCharaEntry>("BattleChara", 0);
        RequireOffset<ClientObjectManager.BattleCharaEntry>("ObjectKind", 8);
        RequireOffset<SceneCameraManager>("_cameras", 0x58);
        RequireOffset<OffscreenRenderingManager>("_cameras", 0xC8);
        RequireOffset<SceneCamera>("ObjectFlags", 0x38);
        RequireOffset<SceneCamera>("Position", 0x50);
        RequireOffset<SceneCamera>("LookAtVector", 0x80);
        RequireOffset<SceneCamera>("Vector_1", 0x90);
        RequireOffset<SceneCamera>("RenderCamera", 0xE0);
        RequireOffset<SceneCamera>("Flags", 0xF0);
        RequireOffset<RenderCamera>("AspectRatio", 0x1F0);
        RequireOffset<RenderCamera>("NearPlane", 0x1F4);
        RequireOffset<RenderCamera>("FarPlane", 0x1F8);
        RequireOffset<RenderCamera>("OrthoHeight", 0x1FC);
        RequireOffset<RenderCamera>("IsOrtho", 0x200);
        RequireOffset<NativeCharacter>(nameof(NativeCharacter.Timeline), 0xA30);
        RequireOffset<NativeCharacter>(nameof(NativeCharacter.EmoteController), 0x630);
        RequireOffset<FFXIVClientStructs.FFXIV.Client.Game.Control.EmoteController>("OwnerObject", 0x8);
        RequireOffset<NativeCharacter>(nameof(NativeCharacter.Vfx), 0x1988);
        RequireOffset<GameObject>(nameof(GameObject.ObjectIndex), 0x8C);
        RequireOffset<GameObject>(nameof(GameObject.BaseId), 0x84);
        RequireOffset<GameObject>(nameof(GameObject.ObjectKind), 0x90);
        RequireOffset<GameObject>(nameof(GameObject.SharedGroupLayoutInstance), 0x108);
        RequireOffset<GameObject>(nameof(GameObject.RenderFlags), 0x118);
        RequireOffset<NativeCharacter>(nameof(NativeCharacter.CharacterSetup), 0x1B10);
        RequireOffset<CharacterSetupContainer>(nameof(CharacterSetupContainer.OwnerObject), 0x8);
        RequireOffset<NativeCharacter>(nameof(NativeCharacter.ModelContainer), 0x1B28);
        RequireOffset<ModelContainer>(nameof(ModelContainer.OwnerObject), 0x8);
        RequireOffset<ModelContainer>(nameof(ModelContainer.ModelCharaId), 0x10);
        RequireOffset<ModelContainer>(nameof(ModelContainer.ModelCharaId_2), 0x18);
        RequireOffset<ModelContainer>(nameof(ModelContainer.ModelSkeletonId_2), 0x1C);
        RequireOffset<NativeCharacter>(nameof(NativeCharacter.Effects), 0x1A90);
        RequireOffset<EffectContainer>(nameof(EffectContainer.OwnerObject), 0x8);
        RequireOffset<NativeCharacter>(nameof(NativeCharacter.OrnamentData), 0x960);
        RequireOffset<OrnamentContainer>(nameof(OrnamentContainer.OwnerObject), 0x8);
        RequireOffset<NativeCharacter>(nameof(NativeCharacter.DrawData), 0x6F8);
        RequireOffset<DrawDataContainer>(nameof(DrawDataContainer.OwnerObject), 0x8);
        RequireOffset<NativeCharacter>(nameof(NativeCharacter.Mount), 0x670);
        RequireOffset<MountContainer>(nameof(MountContainer.OwnerObject), 0x8);
        RequireOffset<MountContainer>(nameof(MountContainer.MountObject), 0x10);
        RequireOffset<MountContainer>(nameof(MountContainer.MountId), 0x18);
        RequireOffset<NativeCharacter>(nameof(NativeCharacter.CompanionData), 0x6D8);
        RequireOffset<CompanionContainer>(nameof(CompanionContainer.OwnerObject), 0x8);
        RequireOffset<CompanionContainer>(nameof(CompanionContainer.CompanionObject), 0x10);
        RequireOffset<CompanionContainer>(nameof(CompanionContainer.CompanionId), 0x18);
        RequireOffset<Companion>(nameof(Companion.Owner), 0x2400);
        RequireOffset<NativeCharacter>(nameof(NativeCharacter.ChildObject), 0x22F8);
        RequireOffset<NativeCharacter>(nameof(NativeCharacter.Transformation), 0x9D8);
        RequireOffset<TransformationContainer>(nameof(TransformationContainer.OwnerObject), 0x8);
        RequireOffset<TimelineContainer>(nameof(TimelineContainer.Flags3), 0x34E);
        RequireOffset<TimelineContainer>(nameof(TimelineContainer.OwnerObject), 0x8);
        RequireOffset<TimelineContainer>(nameof(TimelineContainer.TimelineSequencer), 0x10);
        RequireOffset<FFXIVClientStructs.FFXIV.Client.Game.Control.ActionTimelineSequencer>("Parent", 0x1C8);
        RequireOffset<VfxContainer>(nameof(VfxContainer.OwnerObject), 0x8);
        RequireOffset<CharacterBase>(nameof(CharacterBase.SlotCount), 0x9C);
        RequireOffset<CharacterBase>(nameof(CharacterBase.Models), 0xA8);
        RequireOffset<Model>(nameof(Model.ModelResourceHandle), 0x30);
        RequireOffset<Model>(nameof(Model.RenderModelCallback), 0x48);
        RequireOffset<ModelRenderer.Callback>(nameof(ModelRenderer.Callback.ThisArg), 0x8);
        RequireOffset<Model>(nameof(Model.SlotIndex), 0x118);
        RequireOffset<Model>(nameof(Model.EnabledAttributeIndexMask), 0xAC);
        RequireOffset<Model>(nameof(Model.EnabledShapeKeyIndexMask), 0xC8);
        RequireOffset<CharaViewModelData>("_equipmentModelIds", 0x20);
        RequireOffset<CharaViewModelData>("_glassesIds", 0x1A);
    }

    private static void RequireOffset<T>(string name, int offset) where T : struct
    {
        var field = typeof(T).GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field?.GetCustomAttribute<FieldOffsetAttribute>()?.Value != offset)
            throw new InvalidOperationException("The standalone preview field changed: " + typeof(T).Name + "." + name);
    }

    private void RequirePointer(nint pointer, int rva)
    {
        if (pointer != moduleBase + rva)
            throw new InvalidOperationException("The standalone preview native binding changed.");
    }

    private void AddProof(PEReader disk, int rva, int length)
    {
        var bytes = disk.GetSectionData(rva).GetContent(0, length).ToArray();
        if (!NearbyZoneNativeBinding.Read(moduleBase + rva, length).AsSpan().SequenceEqual(bytes))
            throw new InvalidOperationException($"Standalone preview code changed at RVA 0x{rva:X}.");
        proofs.Add((rva, bytes));
    }

    private void AddResumeProof(PEReader disk, int rva, int length)
    {
        AddProof(disk, rva, length);
        resumeProofs.Add(proofs[^1]);
    }

    private void AddVtableProof(PEReader disk, int rva, int length)
    {
        var header = disk.PEHeaders.PEHeader ?? throw new InvalidOperationException("Missing standalone preview PE header.");
        var bytes = disk.GetSectionData(rva).GetContent(0, length).ToArray();
        if (length % sizeof(ulong) != 0) throw new InvalidOperationException("Invalid standalone preview virtual table length.");
        for (var offset = 0; offset < length; offset += sizeof(ulong))
        {
            var diskPointer = BitConverter.ToUInt64(bytes, offset);
            if (diskPointer < header.ImageBase || diskPointer - header.ImageBase >= (ulong)header.SizeOfImage)
                throw new InvalidOperationException("The standalone preview virtual table target changed.");
            var loadedPointer = checked((ulong)(nuint)moduleBase + (diskPointer - header.ImageBase));
            BitConverter.GetBytes(loadedPointer).CopyTo(bytes, offset);
        }
        if (!NearbyZoneNativeBinding.Read(moduleBase + rva, length).AsSpan().SequenceEqual(bytes))
            throw new InvalidOperationException($"Standalone preview virtual table changed at RVA 0x{rva:X}.");
        proofs.Add((rva, bytes));
    }

    private Hook<T> AddHook<T>(PEReader disk, int rva, int length, T detour, int entryLength = 7) where T : Delegate
    {
        var bytes = disk.GetSectionData(rva).GetContent(0, length).ToArray();
        // TitleEdit's reviewed same-entry Create hook forwards gameplay calls.
        // Reloaded chains it through our Original; every other owner stays rejected.
        byte[]? saved;
        try { saved = ViewSingleItemRendererHookState.ReadSavedEntry(moduleBase + rva, length, rva == 0x1758940); }
        catch (InvalidOperationException error)
        { throw new InvalidOperationException($"Standalone preview hook at RVA 0x{rva:X} is unavailable. " + error.Message, error); }
        var loaded = NearbyZoneNativeBinding.Read(moduleBase + rva, length);
        if (!ViewSingleItemRendererHookState.MatchesSupportedCode(bytes, loaded, saved, entryLength))
            throw new InvalidOperationException($"Standalone preview hook changed at RVA 0x{rva:X}. "
                + ViewSingleItemRendererDiagnostics.DescribeBytes(rva, bytes, loaded) + " "
                + ViewSingleItemRendererDiagnostics.DescribeSavedEntry(saved) + " "
                + ViewSingleItemRendererDiagnostics.DescribeOwners(moduleBase + rva, length));
        var hook = Plugin.GameInterop.HookFromAddress(moduleBase + rva, detour);
        hooks.Add(hook);
        if (hook.BackendName != "Reloaded")
            throw new InvalidOperationException($"Standalone preview hook at RVA 0x{rva:X} requires the reviewed Reloaded backend.");
        hookProofs.Add((hook.Enable, rva, length));
        return hook;
    }

    internal static bool MatchesNativeBytes(nint address, byte[] expected)
    {
        // Recheck access and every byte on each call without allocating a copy
        // of each native function body. Do not cache successful validation.
        NearbyZoneNativeBinding.RequireReadable(address, expected.Length);
        return new ReadOnlySpan<byte>((void*)address, expected.Length).SequenceEqual(expected);
    }

    internal void Validate()
    {
        NearbyZoneNativeBinding.RequireFramework();
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            foreach (var proof in proofs)
                if (!MatchesNativeBytes(moduleBase + proof.Rva, proof.Bytes))
                {
                    preempted = true; RetireFilterSafely();
                    throw new InvalidOperationException("The standalone preview native code changed.");
                }
            if (hooks.Any(hook => hook.IsDisposed || !hook.IsEnabled))
            {
                preempted = true; RetireFilterSafely();
                throw new InvalidOperationException("The standalone preview hooks are unavailable.");
            }
        }
    }

    internal void Capture(NativeCharacter* source)
    {
        Validate();
        lock (sync)
        {
            ClearLocked();
            NearbyZoneNativeBinding.RequireReadable((nint)source, sizeof(NativeCharacter));
            var copy = default(CharaViewModelData);
            copy.CopyFromCharacter(source);
            copy.WeaponHidden = false;
            copy.HeadgearHidden = false;
            appearance = copy; captured = true;
            yaw = pitch = roll = panX = panY = 0; zoom = 1;
        }
    }

    internal byte[] ExportAppearance()
    {
        Validate();
        lock (sync)
        {
            if (!captured) throw new InvalidOperationException("No outfit has been captured.");
            return ViewSingleItemCapture.Encode(appearance);
        }
    }

    internal void Restore(ViewSingleItemCapture capture)
    {
        Validate();
        var copy = capture.Decode();
        lock (sync)
        {
            ClearLocked();
            appearance = copy; captured = true;
            yaw = pitch = roll = panX = panY = 0; zoom = 1;
        }
    }

    // Rebuild the disposable appearance with only the selected item. Merely
    // filtering meshes retains outfit-driven cuts, such as boots trimming legs
    // and a hat choosing cropped hair. Canonical capture/history stays complete.
    private CharaViewModelData CreateLeaseAppearance()
    {
        var copy = appearance;
        var equipmentSlot = EquipmentSlotForChoice(itemSelection);
        for (var index = 0; index < 10; index++)
            if (index != equipmentSlot) copy.EquipmentModelIds[index] = default;
        if (itemSelection != 101)
            for (var index = 0; index < 2; index++) copy.GlassesIds[index] = 0;
        for (var index = 0; index < 3; index++)
        {
            var keep = weaponSelection == 0 && (index != 1 || pairedWeapon)
                || weaponSelection == 1 && index == 1;
            if (!keep) copy.WeaponModelIds[index] = default;
        }
        if (IsStandaloneModel)
        {
            copy.FreeCompanyCrestData = default;
        }
        copy.WeaponHidden = false;
        return copy;
    }

    private static int EquipmentSlotForChoice(int key) => key switch
    {
        2 => 0, 3 => 1, 4 => 2, 6 => 3, 7 => 4, 8 => 5, 9 => 6,
        10 => 7, 11 => 8, 12 => 9, _ => -1,
    };

    private bool DesiredWeaponDrawn => weaponSelection >= 0 && weaponDrawn;

    internal void Select(int key, bool includePairedWeapon, ViewSingleItemOrnament? accessory = null,
        ViewSingleItemCompanion? companion = null)
    {
        Validate();
        lock (sync)
        {
            if (!captured) return;
            var next = key is 0 or 1 ? key : -1;
            var paired = next == 0 && includePairedWeapon;
            var nextAccessorySelection = key == 102;
            var nextAccessory = nextAccessorySelection ? accessory : null;
            nextAccessory?.Validate();
            var nextCompanion = key is 103 or 104 ? companion : null;
            nextCompanion?.Validate();
            if (nextCompanion != null && nextCompanion.Key != key)
                throw new InvalidOperationException("The captured model belongs to another category.");
            if (selectionReady && itemSelection == key && weaponSelection == next && pairedWeapon == paired
                && accessorySelection == nextAccessorySelection && Equals(fashionAccessory, nextAccessory)
                && Equals(previewCompanion, nextCompanion)) return;
            // Releasing the old clone also removes its weapon effects. Merely
            // hiding its meshes would leave those effects in the output texture.
            RetireFilterSafely();
            ReleaseLocked();
            itemSelection = key; weaponSelection = next; pairedWeapon = paired; selectionReady = true;
            accessorySelection = nextAccessorySelection; fashionAccessory = nextAccessory;
            previewCompanion = nextCompanion;
            accessoryModelType = default;
            if (IsFashionAccessory)
            {
                var selectedAccessory = fashionAccessory!;
                var row = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Ornament>().GetRowOrDefault(selectedAccessory.Id);
                var model = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ModelChara>().GetRowOrDefault(selectedAccessory.ModelCharaId);
                if (row == null || row.Value.Model != selectedAccessory.ModelCharaId
                    || model == null || model.Value.Type is not (2 or 3))
                    throw new InvalidOperationException("The captured fashion accessory has no supported standalone model.");
                accessoryModelType = (CharacterBase.ModelType)model.Value.Type;
            }
            else if (IsStandaloneModel)
            {
                var selectedCompanion = previewCompanion!;
                var current = ViewSingleItemCompanions.Create(key, selectedCompanion.Id, out var unavailable);
                if (current == null || !Equals(current, selectedCompanion))
                    throw new InvalidOperationException(string.IsNullOrEmpty(unavailable)
                        ? "The captured minion or mount model changed in the current game data." : unavailable);
                var model = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ModelChara>().GetRowOrDefault(current.ModelCharaId);
                if (model == null || model.Value.Type is not (2 or 3))
                    throw new InvalidOperationException("The captured minion or mount has no supported standalone model.");
                accessoryModelType = (CharacterBase.ModelType)model.Value.Type;
            }
            retryAfter = 0;
            TryLease();
        }
    }

    internal void SetWeaponDrawn(bool drawn)
    {
        Validate();
        lock (sync)
        {
            if (!captured || !selectionReady || weaponSelection < 0 || weaponDrawn == drawn) return;
            RetireFilterSafely();
            // Never start a draw/sheath timeline on a clone we have frozen.
            // Owned cleanup resumes it before deletion; the new clone settles
            // into the requested state before its still image is admitted.
            ReleaseLocked();
            weaponDrawn = drawn;
            retryAfter = 0;
            TryLease();
        }
    }

    private static ClientObjectManager* Objects()
    {
        var manager = ClientObjectManager.Instance();
        NearbyZoneNativeBinding.RequireReadable((nint)manager, sizeof(ClientObjectManager));
        return manager;
    }

    private static nint SceneSlot(uint index)
    {
        var manager = SceneCameraManager.Instance();
        NearbyZoneNativeBinding.RequireReadable((nint)manager, sizeof(SceneCameraManager));
        if (index >= SlotCount) throw new InvalidOperationException("Invalid standalone preview slot.");
        return (nint)manager + SceneSlotsOffset + checked((int)index) * 8;
    }

    private static int ContextCount(uint index)
    {
        var manager = Manager.Instance();
        var address = (nint)manager + ContextCountsOffset + checked((int)index) * 4;
        if (manager == null) throw new InvalidOperationException("The preview renderer is unavailable.");
        NearbyZoneNativeBinding.RequireReadable(address, 4);
        return Marshal.ReadInt32(address);
    }

    private void TryLease()
    {
        if (disposed || cleanupBlocked || !captured || !selectionReady || view != null || Environment.TickCount64 < retryAfter) return;
        if (accessorySelection && !IsFashionAccessory)
        {
            StatusText = fashionAccessory == null ? "No fashion accessory was captured for this player."
                : "The player was not using a fashion accessory when captured.";
            ReadinessDiagnostic = $"stage=accessory-unavailable; captured={fashionAccessory != null}";
            return;
        }
        if ((itemSelection is 103 or 104) && !IsStandaloneModel)
        {
            StatusText = previewCompanion == null ? "No " + StandaloneModelName + " was captured."
                : "The player had no visible " + StandaloneModelName + " when captured.";
            ReadinessDiagnostic = $"stage=companion-unavailable; key={itemSelection}; captured={previewCompanion != null}";
            return;
        }
        if (IsStandaloneModel && accessoryModelType is not (CharacterBase.ModelType.DemiHuman or CharacterBase.ModelType.Monster))
            throw new InvalidOperationException("The captured " + StandaloneModelName + " model type was not admitted.");
        var manager = Objects();
        for (var index = SlotCount; index-- > 0;)
        {
            ref var entry = ref manager->BattleCharas[checked((int)(FirstObject + index))];
            // The offscreen camera and output texture may persist after native Release.
            // Empty object entry, scene slot and render-context count establish admission.
            if (foreignOperations[index] != 0 || entry.BattleChara != null || (byte)entry.ObjectKind != 0
                || Marshal.ReadIntPtr(SceneSlot(index)) != 0 || ContextCount(index) != 0) continue;
            var space = IMemorySpace.GetUISpace();
            NearbyZoneNativeBinding.RequireReadable((nint)space, sizeof(IMemorySpace));
            var memory = (CharaView*)space->Malloc((ulong)sizeof(CharaView), 16);
            if (memory == null) throw new InvalidOperationException("The standalone preview allocation failed.");
            var adopted = false;
            try
            {
                NearbyZoneNativeBinding.RequireWritable((nint)memory, sizeof(CharaView));
                *memory = default;
                view = memory; slot = index; character = 0; preempted = false; constructed = false;
                ResetStillState();
                adopted = true;
                view->Ctor();
                constructed = true;
                if (Marshal.ReadIntPtr((nint)view) != moduleBase + 0x21A0D38)
                    throw new InvalidOperationException("The standalone preview virtual table changed.");
                // Native Initialize deletes an existing slot. Recheck while holding our
                // lease lock and before the call; foreign requests use the preemption hooks.
                if (foreignOperations[index] != 0 || entry.BattleChara != null || (byte)entry.ObjectKind != 0
                    || Marshal.ReadIntPtr(SceneSlot(index)) != 0 || ContextCount(index) != 0)
                { ReleaseLocked(); continue; }
                RunOwned(1, () => view->Initialize(null, index, 0));
                character = (nint)manager->GetObjectByIndex(checked((ushort)(FirstObject + index)));
                if (character == 0) throw new InvalidOperationException("The standalone preview character allocation failed.");
                leaseAppearance = CreateLeaseAppearance();
                var copy = leaseAppearance;
                view->SetModelData(&copy);
                Plugin.Log.Information("[View Item] Scene loadout: selection={Selection}; paired={Paired}; drawn={Drawn}; weaponIds={Main}/{Off}/{Aux}; slot={Slot}; accessory={Accessory}; accessoryModel={AccessoryModel}; equipmentSlot={EquipmentSlot}; geometry=isolated-item.",
                    itemSelection, pairedWeapon, DesiredWeaponDrawn, copy.WeaponModelIds[0].Id,
                    copy.WeaponModelIds[1].Id, copy.WeaponModelIds[2].Id, slot,
                    fashionAccessory?.Id ?? 0, fashionAccessory?.ModelCharaId ?? 0, EquipmentSlotForChoice(itemSelection));
                leaseStarted = Environment.TickCount64;
                StatusText = "Loading the captured appearance in the standalone preview...";
                return;
            }
            catch
            {
                if (adopted) { if (view == memory) ReleaseLocked(); }
                else IMemorySpace.Free(memory, (ulong)sizeof(CharaView));
                throw;
            }
        }
        retryAfter = Environment.TickCount64 + 250;
        StatusText = "Native preview windows are using all available slots. Close a preview window to continue.";
    }

    private void RunOwned(int operation, Action action)
    {
        var previousThread = ownThread; var previousOperation = ownOperation;
        ownThread = Environment.CurrentManagedThreadId; ownOperation = operation;
        try { action(); }
        finally { ownThread = previousThread; ownOperation = previousOperation; }
    }

    private bool OwnOperation(int operation) => ownThread == Environment.CurrentManagedThreadId && ownOperation == operation;

    internal void Tick(long frame)
    {
        Validate();
        lock (sync)
        {
            if (!captured) return;
            if (preempted || (view != null && !OwnsCharacter())) ReleaseLocked();
            TryLease();
            if (view == null) return;
            RunOwned(3, () => view->Render(unchecked((uint)frame)));
            if (preempted || !OwnsCharacter()) { ReleaseLocked(); return; }
            if (IsStandaloneModel && !accessoryProjected)
            {
                ProjectFashionAccessory();
                return;
            }
            if (view->State != 6 || view->CharacterDataCopied || view->CharacterLoaded)
            {
                SuspendAnimationTimeline();
                animationDiagnostic = $"animation=loading; slot={slot}";
                stillReadyAt = 0;
                StatusText = Environment.TickCount64 - leaseStarted > 10000
                    ? "The captured model is still loading. Choose another inspected item or reopen View Item."
                    : "Loading the captured appearance in the standalone preview...";
                return;
            }
            if (!ApplyWeaponState()) { SuspendAnimationTimeline(); return; }
            PauseOwnedAnimation();
            UpdateAnimationTimeline();
            ApplyCamera();
        }
    }

    private void ResetStillState()
    {
        stillReadyAt = 0; stillGeometry = pausedGeometry = 0; animationPaused = ownsPauseReason = false;
        fullGeometryModel = fullGeometryResource = 0;
        fullGeometryAttributes = fullGeometryShapes = 0; fullGeometryVisible = false;
        weaponStateRetryAt = 0; weaponStateAttempts = 0;
        accessoryProjected = false;
        animationDiagnostic = "animation=not-ready";
        ResetAnimationTimeline();
    }

    private void ProjectFashionAccessory()
    {
        if (view == null || !IsStandaloneModel || accessoryProjected) return;
        var modelId = StandaloneModelId;
        ReadinessDiagnostic = $"stage=accessory-projection; key={itemSelection}; slot={slot}; state={view->State}; model={modelId}";
        if (view->State < 3)
        {
            StatusText = "Preparing the standalone " + StandaloneModelName + " scene...";
            return;
        }
        // Render's initial appearance stage falls into model-ready in the same
        // call. Its first root creation returns false and leaves State3. Replace
        // that initial root now, before any still pause or filter is published;
        // the next ordinary Render creates and binds the selected model root.
        // Minions and mounts use the same lease, never world summon/mount calls.
        // A changed sequence is rejected instead of writing native view state.
        if (view->State != 3 || view->CharacterDataCopied || view->CharacterLoaded)
            throw new InvalidOperationException($"The standalone {StandaloneModelName} initialization sequence changed (state={view->State}, appearancePending={view->CharacterDataCopied}, modelPending={view->CharacterLoaded}).");
        if (Marshal.ReadIntPtr((nint)view) != moduleBase + 0x21A0D38 || view->CameraManager == 0
            || view->Camera == null || Marshal.ReadIntPtr(SceneSlot(slot)) != (nint)view->Camera || ContextCount(slot) != 1)
            throw new InvalidOperationException("The standalone " + StandaloneModelName + " render context could not be confirmed.");
        NearbyZoneNativeBinding.RequireReadable((nint)view->CameraManager, 0x20);
        if (Marshal.ReadIntPtr((nint)view->CameraManager) != moduleBase + 0x214CDE8)
            throw new InvalidOperationException("The standalone " + StandaloneModelName + " render context ownership changed.");
        RequireFashionValue("StillOwner", ConfirmStillOwner() ? 1 : 0, 1);
        RequireFashionValue("AnimationPaused", animationPaused ? 1 : 0, 0);
        RequireFashionValue("OwnsPauseReason", ownsPauseReason ? 1 : 0, 0);
        RequireFashionValue("PauseFlags", Marshal.ReadByte(character + 0x1BA4), 0);
        // ClientObjectManager indices 0xF0..0xF7 map to GameObject indices
        // 0x1B8..0x1BF. Native Create passes index + 0xC8 to GameObject.Initialize.
        RequireFashionValue("GameObject.ObjectIndex", unchecked((ushort)Marshal.ReadInt16(character + 0x8C)), FirstObject + slot + 0xC8);
        RequireFashionValue("CharacterFlags&0xC", Marshal.ReadByte(character + 0x190) & 12, 4);
        RequireFashionValue("DrawFlags&0x40", Marshal.ReadByte(character + 0x9A) & 0x40, 0x40);
        RequireFashionValue("CharacterSetup.OwnerObject", Marshal.ReadIntPtr(character + 0x1B18), character);
        RequireFashionValue("ModelContainer.OwnerObject", Marshal.ReadIntPtr(character + 0x1B30), character);
        RequireFashionValue("Effects.OwnerObject", Marshal.ReadIntPtr(character + 0x1A98), character);
        RequireFashionValue("DrawData.OwnerObject", Marshal.ReadIntPtr(character + 0x700), character);
        RequireFashionValue("Mount.OwnerObject", Marshal.ReadIntPtr(character + 0x678), character);
        RequireFashionValue("OrnamentData.OwnerObject", Marshal.ReadIntPtr(character + 0x968), character);
        RequireFashionValue("Transformation.OwnerObject", Marshal.ReadIntPtr(character + 0x9E0), character);
        RequireFashionValue("ChildObject", Marshal.ReadIntPtr(character + 0x22F8), 0);
        RequireFashionValue("SharedGroupLayoutInstance", Marshal.ReadIntPtr(character + 0x108), 0);
        // Native initialization sets 0x900 (including hidden nameplate). These
        // visibility bits do not imply a foreign owner; keep rejecting model
        // hiding and all other flags without changing the clone's native flags.
        RequireFashionValue("RenderFlags&~0x900", Marshal.ReadInt64(character + 0x118) & ~0x900L, 0);
        RequireFashionValue("ModelCharaId_2", Marshal.ReadInt32(character + 0x1B40), -1);
        RequireFashionValue("ModelSkeletonId_2", Marshal.ReadInt32(character + 0x1B44), 0);
        var clone = (NativeCharacter*)character;
        NearbyZoneNativeBinding.RequireWritable(character, sizeof(NativeCharacter));
        var initialDraw = (nint)clone->DrawObject;
        if (initialDraw != 0)
        {
            NearbyZoneNativeBinding.RequireWritable(initialDraw, sizeof(CharacterBase));
            if (((CharacterBase*)initialDraw)->GetModelType() != CharacterBase.ModelType.Human)
                throw new InvalidOperationException("The " + StandaloneModelName + " initial standalone model changed.");
            RunOwned(3, () => disableDraw(character));
        }
        if (!ConfirmStillOwner() || clone->DrawObject != null)
            throw new InvalidOperationException("The " + StandaloneModelName + " initial standalone model could not be retired.");
        RunOwned(3, () => setupModel(character + 0x1B10, modelId, 0, 0, 0, 1f));
        if (!ConfirmStillOwner() || clone->DrawObject != null
            || Marshal.ReadInt32(character + 0x1B38) != modelId
            || Marshal.ReadByte(character + 0x1B48) != (byte)accessoryModelType)
            throw new InvalidOperationException("The " + StandaloneModelName + " standalone model setup could not be confirmed.");
        accessoryProjected = true;
        animationDiagnostic = $"animation=accessory-model-loading; key={itemSelection}; slot={slot}; model={modelId}; type={accessoryModelType}";
        StatusText = "Loading the captured " + StandaloneModelName + " in the standalone preview...";
        Plugin.Log.Information("[View Item] Standalone model projection: key={Key}; row={Row}; model={Model}; type={Type}; slot={Slot}; initialRootRetired={Retired}; state={State}.",
            itemSelection, IsFashionAccessory ? fashionAccessory!.Id : previewCompanion!.Id, modelId, accessoryModelType, slot, initialDraw != 0, view->State);
    }

    private void RequireFashionValue(string field, long actual, long expected)
    {
        if (actual == expected) return;
        ReadinessDiagnostic = $"stage=accessory-owner-rejected; field={field}; actual=0x{actual:X}; expected=0x{expected:X}; "
            + $"slot={slot}; clientIndex={FirstObject + slot}; character=0x{character:X}; {animationDiagnostic}";
        throw new InvalidOperationException("The standalone " + StandaloneModelName + " clone ownership could not be confirmed. " + ReadinessDiagnostic);
    }

    private bool ApplyWeaponState()
    {
        if (!ConfirmStillOwner()) return false;
        if (IsStandaloneModel) return accessoryProjected;
        var clone = (NativeCharacter*)character;
        var isolatedOffhand = weaponSelection == 1 && leaseAppearance.WeaponModelIds[0].Id == 0
            && leaseAppearance.WeaponModelIds[1].Id != 0;
        if (isolatedOffhand && !OffhandGeometryReady())
        {
            animationPaused = false; stillReadyAt = 0;
            StatusText = "Waiting for the standalone off-hand model to load...";
            return false;
        }
        if (clone->IsWeaponDrawn == DesiredWeaponDrawn) return true;
        if (animationPaused || ownsPauseReason || Marshal.ReadByte(character + 0x1BA4) != 0)
        {
            animationPaused = false;
            RetireFilterSafely();
            animationDiagnostic = $"animation=weapon-state-paused; slot={slot}; requestedDrawn={DesiredWeaponDrawn}; actualDrawn={clone->IsWeaponDrawn}";
            StatusText = "Waiting for an unpaused standalone model before changing its weapon state...";
            return false;
        }
        if (!isolatedOffhand)
        {
            // Main-hand admission and the one-second throttle stay native.
            RunOwned(3, () => view->ToggleDrawWeapon(DesiredWeaponDrawn));
            return true;
        }
        // CharaView requires a ready main-hand attachment, which an isolated
        // off-hand must not load. Use its exact underlying timeline routine,
        // with the same animation argument, only for this confirmed owned case.
        if (!StillGeometryReady(out _) || Environment.TickCount64 < weaponStateRetryAt) return false;
        if (weaponStateAttempts >= 3)
            throw new InvalidOperationException("The standalone off-hand weapon state could not be confirmed.");
        if (Marshal.ReadIntPtr(character + 0x638) != character)
            throw new InvalidOperationException("The standalone off-hand emote owner changed.");
        if (Marshal.ReadByte(character + 0x1CD) == 0xA)
            throw new InvalidOperationException("This off-hand animation uses linked actors and cannot be isolated.");
        NearbyZoneNativeBinding.RequireWritable(character, sizeof(NativeCharacter));
        if (!ConfirmStillOwner()) return false;
        weaponStateAttempts++;
        weaponStateRetryAt = Environment.TickCount64 + 1000;
        drawWeapon(character + 0xA30, DesiredWeaponDrawn ? (byte)1 : (byte)0, 1);
        stillReadyAt = 0;
        Plugin.Log.Information("[View Item] Off-hand weapon state: drawn={Drawn}; attempt={Attempt}; slot={Slot}.",
            DesiredWeaponDrawn, weaponStateAttempts, slot);
        return ConfirmStillOwner() && clone->IsWeaponDrawn == DesiredWeaponDrawn;
    }

    private bool OffhandGeometryReady()
    {
        var address = Marshal.ReadIntPtr(character + 0x780);
        var state = Marshal.ReadByte(character + 0x7DC);
        animationDiagnostic = $"animation=offhand-geometry; slot={slot}; draw=0x{address:X}; state={state}";
        if (address == 0 || state != 3) return false;
        NearbyZoneNativeBinding.RequireReadable(address, sizeof(CharacterBase));
        var draw = (CharacterBase*)address;
        var type = draw->GetModelType();
        animationDiagnostic += $"; type={type}; slots={draw->SlotCount}; models=0x{(nint)draw->Models:X}";
        if (type != CharacterBase.ModelType.Weapon || draw->SlotCount <= 0
            || draw->SlotCount > 32 || draw->Models == null) return false;
        NearbyZoneNativeBinding.RequireReadable((nint)draw->Models, checked(draw->SlotCount * sizeof(nint)));
        for (var index = 0; index < draw->SlotCount; index++)
            if (draw->Models[index] != null) return true;
        animationDiagnostic += "; populated=False";
        return false;
    }

    private bool ConfirmStillOwner() => ConfirmStillOwnerCore(false);

    private bool ConfirmStillOwnerCore(bool allowPreempted)
    {
        if (view == null || (preempted && !allowPreempted) || !OwnsCharacter()) return false;
        NearbyZoneNativeBinding.RequireReadable(character, sizeof(NativeCharacter));
        // The pause routine can recurse through these links. This value-only UI
        // clone must have none; never freeze an inspected or gameplay character.
        if (Marshal.ReadIntPtr(character) != moduleBase + 0x21A4A98
            || (Marshal.ReadByte(character + 0x190) & 4) == 0
            || Marshal.ReadIntPtr(character + 0x1B88) != character
            || Marshal.ReadIntPtr(character + 0x1990) != character
            || Marshal.ReadIntPtr(character + 0xA38) != character || Marshal.ReadIntPtr(character + 0xC08) != character
            || Marshal.ReadIntPtr(character + 0x680) != 0 || Marshal.ReadIntPtr(character + 0x970) != 0
            || Marshal.ReadIntPtr(character + 0xA00) != 0 || Marshal.ReadIntPtr(character + 0x6E8) != 0
            || Marshal.ReadInt16(character + 0x688) != 0 || Marshal.ReadInt16(character + 0x978) != 0)
        {
            animationDiagnostic = $"animation=owner-rejected; slot={slot}; character=0x{character:X}";
            StatusText = "Waiting for a confirmed standalone model before freezing its animation...";
            return false;
        }
        return true;
    }

    private bool StillGeometryReady(out ulong signature)
    {
        signature = 14695981039346656037UL;
        var clone = (NativeCharacter*)character;
        var root = (nint)clone->DrawObject;
        if (root == 0) return false;
        for (var part = -1; part < 3; part++)
        {
            var drawAddress = part < 0 ? root : Marshal.ReadIntPtr(character + 0x710 + part * 0x70);
            signature = unchecked((signature ^ (ulong)(nuint)drawAddress) * 1099511628211UL);
            if (drawAddress == 0) continue;
            NearbyZoneNativeBinding.RequireReadable(drawAddress, sizeof(CharacterBase));
            // Same native loading checks used by ToggleDrawWeapon. Freezing a
            // pending draw would lock a weapon midway through its equip motion.
            if ((Marshal.ReadInt32(drawAddress + 0x2F0) & 0x1F) != 0
                || Marshal.ReadInt32(drawAddress + 0x920) != 0 || Marshal.ReadInt32(drawAddress + 0x3C8) != 0)
                return false;
            var draw = (CharacterBase*)drawAddress;
            if (draw->SlotCount < 0 || draw->SlotCount > 32) return false;
            if (draw->SlotCount == 0 || draw->Models == null)
            {
                if (part < 0) return false;
                continue;
            }
            NearbyZoneNativeBinding.RequireReadable((nint)draw->Models, checked(draw->SlotCount * sizeof(nint)));
            for (var model = 0; model < draw->SlotCount; model++)
                signature = unchecked((signature ^ (ulong)(nuint)draw->Models[model]) * 1099511628211UL);
        }
        return true;
    }

    private bool ConfirmPausedDraws()
    {
        var root = (nint)((NativeCharacter*)character)->DrawObject;
        if (root == 0) return false;
        for (var part = -1; part < 3; part++)
        {
            var draw = part < 0 ? root : Marshal.ReadIntPtr(character + 0x710 + part * 0x70);
            if (draw == 0) continue;
            // Match native pause's attachment eligibility, including hidden or
            // pending copies which the game deliberately omits from pausing.
            if (part >= 0 && (Marshal.ReadByte(character + 0x772 + part * 0x70) & 4) != 0
                && Marshal.ReadByte(character + 0x76C + part * 0x70) != 3) continue;
            NearbyZoneNativeBinding.RequireReadable(draw, sizeof(CharacterBase));
            if ((Marshal.ReadByte(draw + 0x9B4) & 1) == 0) return false;
        }
        return true;
    }

    private void RequirePauseDrawsWritable()
    {
        var root = (nint)((NativeCharacter*)character)->DrawObject;
        if (root != 0) NearbyZoneNativeBinding.RequireWritable(root, sizeof(CharacterBase));
        for (var part = 0; part < 3; part++)
        {
            var draw = Marshal.ReadIntPtr(character + 0x710 + part * 0x70);
            if (draw != 0) NearbyZoneNativeBinding.RequireWritable(draw, sizeof(CharacterBase));
        }
    }

    private bool ApplyFullItemVisibility()
    {
        // Empty slots use EQP row 1, which still trims elbows and knees. Run
        // the native selected-slot calculation with its neutral EQP default;
        // it keeps intrinsic IMC/race/customize rules instead of forcing masks.
        // Head has no remaining neighbor cuts after loadout isolation. Keep
        // its own race permissions; neutral head flags would hide some hats.
        UpdateItemVisibilityDelegate? update = itemSelection switch
        {
            3 => bodyVisibility, 4 => handsVisibility, 6 => legsVisibility, 7 => feetVisibility,
            8 => earringsVisibility, 9 => necklaceVisibility, 10 => braceletVisibility,
            11 => rightRingVisibility, 12 => leftRingVisibility, 100 => hairVisibility, _ => null,
        };
        if (update == null) return true;
        if (!ConfirmStillOwner()) return false;
        var draw = (CharacterBase*)((NativeCharacter*)character)->DrawObject;
        if (draw == null) return false;
        NearbyZoneNativeBinding.RequireReadable((nint)draw, sizeof(CharacterBase));
        if (draw->GetModelType() != CharacterBase.ModelType.Human)
            throw new InvalidOperationException("The full item preview no longer owns a human model.");
        NearbyZoneNativeBinding.RequireReadable((nint)draw, sizeof(Human));
        var selectedSlot = itemSelection == 100 ? 10 : EquipmentSlotForChoice(itemSelection);
        if (draw->SlotCount <= selectedSlot || draw->SlotCount > 32 || draw->Models == null)
            throw new InvalidOperationException("The full item preview model collection changed.");
        NearbyZoneNativeBinding.RequireReadable((nint)draw->Models, checked(draw->SlotCount * sizeof(nint)));
        var model = draw->Models[selectedSlot];
        // A genuinely absent model still uses the existing MissingModels result.
        if (model == null) return true;
        NearbyZoneNativeBinding.RequireReadable((nint)model, sizeof(Model));
        if (model->ModelResourceHandle == null || model->RenderModelCallback == null) return false;
        NearbyZoneNativeBinding.RequireReadable((nint)model->RenderModelCallback, sizeof(ModelRenderer.Callback));
        if (model->SlotIndex != (uint)selectedSlot || (nint)model->RenderModelCallback->ThisArg != (nint)draw)
            throw new InvalidOperationException("The full item preview model owner changed.");
        var resource = (nint)model->ModelResourceHandle;
        NearbyZoneNativeBinding.RequireReadable(resource, sizeof(ModelResourceHandle));
        var visible = (Marshal.ReadInt32((nint)model + 0x28) & 1) != 0;
        if (fullGeometryModel == (nint)model && fullGeometryResource == resource
            && fullGeometryAttributes == model->EnabledAttributeIndexMask
            && fullGeometryShapes == model->EnabledShapeKeyIndexMask && fullGeometryVisible == visible) return true;
        NearbyZoneNativeBinding.RequireWritable((nint)model, sizeof(Model));
        if (!ConfirmStillOwner()) return false;
        RetireFilterSafely();
        var previousAttributes = model->EnabledAttributeIndexMask;
        var previousShapes = model->EnabledShapeKeyIndexMask;
        ulong equipmentFlags = 0x003FE00070603F00;
        update((Human*)draw, &equipmentFlags);
        fullGeometryModel = (nint)model; fullGeometryResource = resource;
        fullGeometryAttributes = model->EnabledAttributeIndexMask;
        fullGeometryShapes = model->EnabledShapeKeyIndexMask;
        fullGeometryVisible = (Marshal.ReadInt32((nint)model + 0x28) & 1) != 0;
        Plugin.Log.Information("[View Item] Full item visibility: selection={Selection}; slot={Slot}; model=0x{Model:X}; attributes=0x{BeforeAttributes:X}->0x{AfterAttributes:X}; shapes=0x{BeforeShapes:X}->0x{AfterShapes:X}; visible={Visible}; equipmentFlags=0x{EquipmentFlags:X}.",
            itemSelection, selectedSlot, fullGeometryModel, previousAttributes, fullGeometryAttributes,
            previousShapes, fullGeometryShapes, fullGeometryVisible, equipmentFlags);
        return true;
    }

    private void PauseOwnedAnimation()
    {
        animationReady = false;
        if (!ConfirmStillOwner()) { animationPaused = false; stillReadyAt = 0; return; }
        var clone = (NativeCharacter*)character;
        var weaponPresent = leaseAppearance.WeaponModelIds[0].Id != 0 || leaseAppearance.WeaponModelIds[1].Id != 0;
        if ((weaponPresent && clone->IsWeaponDrawn != DesiredWeaponDrawn) || !StillGeometryReady(out var geometry))
        {
            animationPaused = false; stillReadyAt = 0;
            animationDiagnostic = $"animation=geometry-pending; slot={slot}; weaponRequired={weaponPresent}; weaponDrawn={clone->IsWeaponDrawn}; requestedDrawn={DesiredWeaponDrawn}";
            StatusText = "Waiting for the standalone model and weapon attachments to settle...";
            return;
        }
        var now = Environment.TickCount64;
        if (stillReadyAt == 0 || stillGeometry != geometry)
        {
            stillGeometry = geometry; stillReadyAt = now; animationPaused = false;
        }
        if (now - stillReadyAt < 1000)
        {
            animationDiagnostic = $"animation=settling; slot={slot}; stableMs={now - stillReadyAt}";
            StatusText = "Preparing a still view of the selected item...";
            return;
        }
        if (!ApplyFullItemVisibility())
        {
            animationPaused = false; stillReadyAt = 0;
            StatusText = "Waiting for the selected item's complete model...";
            return;
        }
        if (animationPlaying)
        {
            // Playback releases only our pause reason after the same ownership,
            // geometry and full-item visibility checks used by the still view.
            ResumeOwnedAnimation();
            animationReady = !ownsPauseReason && Marshal.ReadByte(character + 0x1BA4) == 0;
            animationDiagnostic = $"animation={(animationReady ? "playing" : "externally-paused")}; slot={slot}; character=0x{character:X}";
            if (!animationReady) StatusText = "Waiting for the standalone animation to resume...";
            return;
        }
        var nativePaused = (Marshal.ReadByte(character + 0x1BA4) & 0x10) != 0 && ConfirmPausedDraws();
        if (!nativePaused || pausedGeometry != geometry)
        {
            if (!ConfirmStillOwner()) return;
            NearbyZoneNativeBinding.RequireWritable(character, sizeof(NativeCharacter));
            RequirePauseDrawsWritable();
            // Only our lease is mutated. Native pause freezes the actor scheduler,
            // weapon attachments, draw physics and attached VFX; the camera and
            // offscreen Render remain independent and continue every frame.
            if ((Marshal.ReadByte(character + 0x1BA4) & 0x10) == 0) ownsPauseReason = true;
            pauseCharacter(character + 0x1B80, 0x10, 0);
            if (!ConfirmStillOwner()) return;
            nativePaused = (Marshal.ReadByte(character + 0x1BA4) & 0x10) != 0 && ConfirmPausedDraws();
            if (nativePaused) pausedGeometry = geometry;
        }
        animationPaused = nativePaused;
        animationReady = nativePaused;
        animationDiagnostic = $"animation={(nativePaused ? "paused" : "pause-unconfirmed")}; slot={slot}; character=0x{character:X}; geometry=0x{geometry:X}";
        if (!nativePaused) StatusText = "Waiting for the standalone animation pause to be confirmed...";
    }

    private void ResumeOwnedAnimation()
    {
        if (!ownsPauseReason || !ConfirmStillOwnerCore(true)) return;
        // Release can run directly from preemption without Tick.Validate. Check
        // the exact cleanup bindings immediately before the owned-only call.
        foreach (var proof in resumeProofs)
            if (!MatchesNativeBytes(moduleBase + proof.Rva, proof.Bytes))
            {
                cleanupBlocked = true;
                throw new InvalidOperationException("The standalone animation cleanup binding changed.");
            }
        NearbyZoneNativeBinding.RequireWritable(character, sizeof(NativeCharacter));
        RequirePauseDrawsWritable();
        if (!ConfirmStillOwnerCore(true)) return;
        resumeCharacter(character + 0x1B80, 0x10, 0);
        ownsPauseReason = false; animationPaused = false;
        animationDiagnostic = $"animation=resumed-before-release; slot={slot}; character=0x{character:X}";
    }

    private bool OwnsCharacter()
    {
        if (view == null || character == 0 || view->ClientObjectIndex != slot || view->ClientObjectId != FirstObject + slot)
            return false;
        return (nint)Objects()->GetObjectByIndex(checked((ushort)(FirstObject + slot))) == character;
    }

    internal NativeCharacter* GetCharacter()
    {
        NearbyZoneNativeBinding.RequireFramework();
        lock (sync)
        {
            if (view == null)
            {
                ReadinessDiagnostic = $"stage=scene-slot-wait; captured={captured}; preempted={preempted}";
                return null;
            }
            if (disposed || preempted || !OwnsCharacter())
            {
                ReadinessDiagnostic = $"stage=scene-owner; slot={slot}; disposed={disposed}; preempted={preempted}; character=0x{character:X}";
                StatusText = "Waiting for a confirmed standalone preview owner...";
                return null;
            }
            ReadinessDiagnostic = $"stage=scene-loading; slot={slot}; state={view->State}; "
                + $"appearancePending={view->CharacterDataCopied}; modelPending={view->CharacterLoaded}; character=0x{character:X}";
            if (view->State != 6 || view->CharacterDataCopied || view->CharacterLoaded)
            { StatusText = "Loading the captured appearance in the standalone preview..."; return null; }
            if (!ConfirmStillOwner() || !AnimationReadyForDisplay())
            {
                animationPaused = false;
                ReadinessDiagnostic = $"stage=scene-animation; slot={slot}; {animationDiagnostic}";
                return null;
            }
            var camera = view->Camera;
            var assignedCamera = Marshal.ReadIntPtr(SceneSlot(slot));
            var contexts = ContextCount(slot);
            ReadinessDiagnostic = $"stage=scene-camera; slot={slot}; camera=0x{(nint)camera:X}; "
                + $"assigned=0x{assignedCamera:X}; contexts={contexts}";
            if (camera == null || assignedCamera != (nint)camera || contexts != 1)
            { StatusText = "Waiting for the standalone preview camera and render context..."; return null; }
            NearbyZoneNativeBinding.RequireReadable((nint)camera, sizeof(SceneCamera));
            var offscreen = OffscreenRenderingManager.Instance();
            NearbyZoneNativeBinding.RequireReadable((nint)offscreen, sizeof(OffscreenRenderingManager));
            if (camera->RenderCamera == null || offscreen->Cameras[checked((int)slot)].Value != camera->RenderCamera)
            {
                ReadinessDiagnostic = $"stage=scene-offscreen-camera; slot={slot}; renderCamera=0x{(nint)camera->RenderCamera:X}";
                StatusText = "Waiting for the standalone camera to render..."; return null;
            }
            var clone = (NativeCharacter*)character;
            NearbyZoneNativeBinding.RequireReadable((nint)clone, sizeof(NativeCharacter));
            var draw = (CharacterBase*)clone->DrawObject;
            if (draw == null)
            {
                ReadinessDiagnostic = $"stage=scene-draw-object; slot={slot}; character=0x{character:X}";
                StatusText = "The standalone character model is still loading."; return null;
            }
            NearbyZoneNativeBinding.RequireReadable((nint)draw, sizeof(CharacterBase));
            var expectedType = IsStandaloneModel ? accessoryModelType : CharacterBase.ModelType.Human;
            var actualType = draw->GetModelType();
            if (actualType != expectedType || (IsStandaloneModel && (!accessoryProjected
                || Marshal.ReadInt32(character + 0x1B38) != StandaloneModelId)))
            {
                ReadinessDiagnostic = $"stage=scene-model-type; slot={slot}; draw=0x{(nint)draw:X}; expected={expectedType}; actual={actualType}; accessoryProjected={accessoryProjected}";
                StatusText = "The standalone preview has an unsupported character model."; return null;
            }
            if (!IsStandaloneModel) NearbyZoneNativeBinding.RequireReadable((nint)draw, sizeof(Human));
            ReadinessDiagnostic = $"stage=scene-ready; slot={slot}; character=0x{character:X}; draw=0x{(nint)draw:X}; camera=0x{(nint)camera:X}";
            return clone;
        }
    }

    internal void Camera(float deltaYaw, float deltaPitch, float deltaZoom, float deltaPanX, float deltaPanY, float deltaRoll = 0)
    {
        Validate();
        if (!float.IsFinite(deltaYaw) || !float.IsFinite(deltaPitch) || !float.IsFinite(deltaZoom)
            || !float.IsFinite(deltaPanX) || !float.IsFinite(deltaPanY) || !float.IsFinite(deltaRoll)) return;
        lock (sync)
        {
            yaw = MathF.IEEERemainder(yaw + Math.Clamp(deltaYaw, -1, 1) * 2, MathF.Tau);
            pitch = Math.Clamp(pitch + Math.Clamp(deltaPitch, -1, 1) * 2, -1.45f, 1.45f);
            roll = MathF.IEEERemainder(roll + Math.Clamp(deltaRoll, -1, 1) * 2, MathF.Tau);
            zoom = Math.Clamp(zoom * MathF.Exp(Math.Clamp(deltaZoom, -4, 4) * 0.15f), 0.1f, 5);
            var height = baseHeight * zoom;
            panX = Math.Clamp(panX + Math.Clamp(deltaPanX, -1, 1) * height, -20, 20);
            panY = Math.Clamp(panY + Math.Clamp(deltaPanY, -1, 1) * height, -20, 20);
            ApplyCamera();
        }
    }

    internal void SetViewport(Vector2 size)
    {
        NearbyZoneNativeBinding.RequireFramework();
        if (!float.IsFinite(size.X) || !float.IsFinite(size.Y) || size.X < 1 || size.Y < 1) return;
        lock (sync) { viewport = Vector2.Clamp(size, new Vector2(1), new Vector2(16384)); ApplyCamera(); }
    }

    internal void ResetCamera()
    {
        Validate();
        lock (sync) { yaw = pitch = roll = panX = panY = 0; zoom = 1; ApplyCamera(); }
    }

    private void ApplyCamera()
    {
        if (view == null || preempted || !OwnsCharacter() || view->Camera == null) return;
        var camera = view->Camera;
        if (Marshal.ReadIntPtr(SceneSlot(slot)) != (nint)camera) { Preempt(); return; }
        NearbyZoneNativeBinding.RequireWritable((nint)camera, sizeof(SceneCamera));
        var render = camera->RenderCamera;
        if (render == null) return;
        NearbyZoneNativeBinding.RequireWritable((nint)render, sizeof(RenderCamera));
        var ratio = view->ZoomRatio;
        if (!float.IsFinite(ratio) || ratio < 0.01f || ratio > 100)
        { StatusText = "Waiting for the standalone camera scale..."; return; }
        var nextBaseHeight = Math.Clamp(5 / ratio, 0.1f, 50);
        var nextTargetHeight = Math.Clamp(1.25f / ratio, 0.0125f, 12.5f);
        // Roll turns the camera's right/up axes around its viewing direction.
        // The item can turn sideways or upside down without changing yaw/pitch.
        var rotation = Quaternion.CreateFromYawPitchRoll(yaw, pitch, roll);
        var right = Vector3.Transform(Vector3.UnitX, rotation);
        var up = Vector3.Transform(Vector3.UnitY, rotation);
        var forward = Vector3.Transform(Vector3.UnitZ, rotation);
        var target = new Vector3(0, nextTargetHeight, 0) + right * panX + up * panY;
        var position = target + forward * 5;
        var height = Math.Clamp(nextBaseHeight * zoom, 0.1f, 50);
        var aspect = Math.Clamp(viewport.X / viewport.Y, 0.1f, 10);
        if (!Finite(right) || !Finite(up) || !Finite(forward) || !Finite(target) || !Finite(position)
            || !float.IsFinite(panX) || !float.IsFinite(panY) || !float.IsFinite(height) || !float.IsFinite(aspect)) return;
        baseHeight = nextBaseHeight;
        camera->Position.X = position.X; camera->Position.Y = position.Y; camera->Position.Z = position.Z;
        camera->LookAtVector.X = target.X; camera->LookAtVector.Y = target.Y; camera->LookAtVector.Z = target.Z;
        camera->Vector_1.X = up.X; camera->Vector_1.Y = up.Y; camera->Vector_1.Z = up.Z;
        camera->ObjectFlags |= 2;
        // Select the native LookAt path (which uses our up vector) and disable
        // automatic aspect. Roll must not depend on inherited camera flags.
        ((uint*)((byte*)camera + 0xF0))[0] = (((uint*)((byte*)camera + 0xF0))[0] | 1u) & ~2u;
        render->IsOrtho = true;
        render->NearPlane = 0.01f; render->FarPlane = 50;
        render->OrthoHeight = height;
        render->AspectRatio = aspect;
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private void Preempt()
    {
        if (view == null || preempted) return;
        preempted = true;
        RetireFilterSafely();
        retryAfter = Environment.TickCount64 + 250;
        StatusText = "A native preview took this slot. Waiting for another standalone slot...";
        // Foreign callers outside the framework retire render admission immediately.
        // The next Tick frees only our own remaining resources using identity checks.
        if (Plugin.Framework.IsInFrameworkUpdateThread) ReleaseLocked();
    }

    private int ReserveForeign(uint index)
    {
        if (index >= SlotCount) return -1;
        var reservation = checked((int)index);
        foreignOperations[reservation]++;
        return reservation;
    }

    private void CompleteForeign(int reservation)
    {
        if (reservation < 0) return;
        try { lock (sync) { foreignOperations[reservation]--; } }
        catch { FailDetour(); }
    }

    private void RetireFilterSafely()
    {
        try { retireFilter(); }
        catch { /* Native forwarding must survive a managed retirement failure. */ }
    }

    private void FailDetour()
    {
        try { lock (sync) { preempted = true; retryAfter = Environment.TickCount64 + 250; } }
        catch { }
        RetireFilterSafely();
    }

    private void InitializeDetour(CharaView* self, AgentInterface* agent, uint index, nint callback)
    {
        var reservation = -1;
        try
        {
            lock (sync)
                if (!(self == view && OwnOperation(1)))
                {
                    reservation = ReserveForeign(index);
                    if (view != null && index == slot) Preempt();
                }
        }
        catch { FailDetour(); }
        try { initializeHook.OriginalDisposeSafe(self, agent, index, callback); }
        finally { CompleteForeign(reservation); }
    }

    private uint CreateDetour(ClientObjectManager* manager, uint index, byte parameter)
    {
        var reservation = -1;
        try
        {
            lock (sync)
                if (!(view != null && index == FirstObject + slot && OwnOperation(1)) && index >= FirstObject)
                {
                    reservation = ReserveForeign(index - FirstObject);
                    if (view != null && index == FirstObject + slot) Preempt();
                }
        }
        catch { FailDetour(); }
        try { return createHook.OriginalDisposeSafe(manager, index, parameter); }
        finally { CompleteForeign(reservation); }
    }

    private void DeleteDetour(ClientObjectManager* manager, ushort index, byte parameter)
    {
        var reservation = -1;
        try
        {
            lock (sync)
                if (!(view != null && index == FirstObject + slot && (OwnOperation(1) || OwnOperation(2))) && index >= FirstObject)
                {
                    reservation = ReserveForeign((uint)index - FirstObject);
                    if (view != null && index == FirstObject + slot) Preempt();
                }
        }
        catch { FailDetour(); }
        try { deleteHook.OriginalDisposeSafe(manager, index, parameter); }
        finally { CompleteForeign(reservation); }
    }

    private byte ContextDetour(nint context, uint index, uint cameraType)
    {
        var reservation = -1;
        try
        {
            lock (sync)
                if (!(view != null && index == slot && context == view->CameraManager && (OwnOperation(1) || OwnOperation(3))))
                {
                    reservation = ReserveForeign(index);
                    if (view != null && index == slot) Preempt();
                }
        }
        catch { FailDetour(); }
        try { return contextHook.OriginalDisposeSafe(context, index, cameraType); }
        finally { CompleteForeign(reservation); }
    }

    private byte SimpleContextDetour(nint context, uint index)
    {
        var reservation = -1;
        try
        {
            lock (sync)
                if (!(view != null && index == slot && context == view->CameraManager && (OwnOperation(1) || OwnOperation(3))))
                {
                    reservation = ReserveForeign(index);
                    if (view != null && index == slot) Preempt();
                }
        }
        catch { FailDetour(); }
        try { return simpleContextHook.OriginalDisposeSafe(context, index); }
        finally { CompleteForeign(reservation); }
    }

    private void CameraAssignmentDetour(OffscreenRenderingManager* manager, uint index, RenderCamera* camera)
    {
        var reservation = -1;
        var ownsCamera = false;
        var lockHeld = false;
        try
        {
            Monitor.Enter(sync, ref lockHeld);
            ownsCamera = view != null && index == slot && view->Camera != null && camera == view->Camera->RenderCamera;
            if (!ownsCamera)
            {
                reservation = ReserveForeign(index);
                if (view != null && index == slot) Preempt();
            }
        }
        catch { FailDetour(); }
        finally
        {
            if (!ownsCamera && lockHeld) { Monitor.Exit(sync); lockHeld = false; }
        }
        // Publication AddRefs the input before replacing its previous camera. Keep
        // our own input alive until that call completes; foreign originals run unlocked.
        try { cameraHook.OriginalDisposeSafe(manager, index, camera); }
        finally
        {
            if (lockHeld) Monitor.Exit(sync);
            CompleteForeign(reservation);
        }
    }

    private void ReleaseLocked()
    {
        if (view == null) return;
        preempted = true;
        RetireFilterSafely();
        var retired = view;
        var retiredCharacter = character;
        var retiredSlot = slot;
        NearbyZoneNativeBinding.RequireWritable((nint)retired, sizeof(CharaView));
        if (!constructed)
        {
            view = null; character = 0;
            IMemorySpace.Free(retired, (ulong)sizeof(CharaView));
            preempted = false;
            return;
        }
        if (Marshal.ReadIntPtr((nint)retired) != moduleBase + 0x21A0D38)
        {
            // Keep the allocation alive rather than free native resources through an
            // unproved virtual table. No further optional scene can be admitted.
            cleanupBlocked = true;
            StatusText = "The standalone preview's native ownership changed. Close View Item.";
            throw new InvalidOperationException("The standalone preview cannot safely release its changed virtual table.");
        }
        if (retired->CameraManager != 0)
        {
            NearbyZoneNativeBinding.RequireReadable((nint)retired->CameraManager, 0x20);
            if (Marshal.ReadIntPtr((nint)retired->CameraManager) != moduleBase + 0x214CDE8)
            { cleanupBlocked = true; throw new InvalidOperationException("The standalone preview context ownership changed."); }
        }
        if (retired->Camera != null)
        {
            NearbyZoneNativeBinding.RequireReadable((nint)retired->Camera, sizeof(SceneCamera));
            if (Marshal.ReadIntPtr((nint)retired->Camera) != moduleBase + 0x216C3D0)
            { cleanupBlocked = true; throw new InvalidOperationException("The standalone preview camera ownership changed."); }
        }
        var manager = Objects();
        var ownsClone = retiredCharacter != 0
            && (nint)manager->GetObjectByIndex(checked((ushort)(FirstObject + retiredSlot))) == retiredCharacter;
        if (ownsClone) ResumeOwnedAnimation();
        var address = SceneSlot(retiredSlot);
        var ownsSceneSlot = retired->Camera != null && Marshal.ReadIntPtr(address) == (nint)retired->Camera;
        if (ownsSceneSlot) NearbyZoneNativeBinding.RequireWritable(address, IntPtr.Size);
        view = null; character = 0; constructed = false;
        // Our pause reason was restored only while the clone still belonged to
        // this lease. Never resume through a retired or replacement character.
        ResetStillState();
        // Skip native Release's unconditional manager delete and global camera clear.
        // Do those operations only while the manager/scene still identifies our objects.
        retired->ClientObjectId = uint.MaxValue;
        try
        {
            if (ownsSceneSlot) Marshal.WriteIntPtr(address, 0);
            if (ownsClone)
                RunOwned(2, () => deleteHook.OriginalDisposeSafe(manager, checked((ushort)(FirstObject + retiredSlot)), 0));
        }
        finally
        {
            // Dtor does not Release. Even a managed clone-delete failure must first
            // drop our renderer context and camera before destroying this allocation.
            try
            {
                retired->Release();
                retired->Dtor(0);
                IMemorySpace.Free(retired, (ulong)sizeof(CharaView));
            }
            catch { cleanupBlocked = true; throw; }
        }
        preempted = false;
    }

    private void ClearLocked()
    {
        ReleaseLocked();
        captured = false; appearance = leaseAppearance = default; retryAfter = 0;
        selectionReady = pairedWeapon = false; itemSelection = weaponSelection = -1; weaponDrawn = true;
        accessorySelection = accessoryProjected = false; fashionAccessory = null; accessoryModelType = default;
        previewCompanion = null;
        StatusText = "Choose an inspected item.";
    }

    internal void Clear()
    {
        NearbyZoneNativeBinding.RequireFramework();
        lock (sync) ClearLocked();
    }

    public void Dispose()
    {
        NearbyZoneNativeBinding.RequireFramework();
        lock (sync)
        {
            if (disposed) return;
            ClearLocked(); disposed = true;
            foreach (var hook in hooks.AsEnumerable().Reverse()) hook.Dispose();
        }
    }
}
