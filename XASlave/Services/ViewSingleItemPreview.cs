using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Dalamud.Bindings.ImGui;
using Dalamud.Hooking;
using Dalamud.Interface.Textures.TextureWraps;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using RenderCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Camera;
using SceneCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Camera;
using SceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

namespace XASlave.Services;

// Examine supplies a value-only appearance once; the filter belongs exclusively
// to our owned scene. Native windows and world characters are never filtered.
internal sealed unsafe partial class ViewSingleItemPreview : IDisposable
{
    private const int MeshGateRva = 0x282A10, MeshGateLength = 0xD8C;
    private readonly nint moduleBase;
    private readonly Hook<MeshRenderDelegate> meshHook;
    private readonly byte[] installedBody;
    private readonly ViewSingleItemScene scene;
    internal ViewSingleItemFurniturePreview Furniture { get; } = new();
    private InspectOutfitSnapshot? source;
    private ViewSingleItemOrnament? ornament;
    private ViewSingleItemCompanion? minion, mount;
    private ViewSingleItemCatalogItem? catalog;
    private Filter? filter;
    private int? selection;
    private bool disposed;
    private long frame, revision, revisionStarted;
    private long invalidRevision;
    private long lastDiagnosticAt, lastFilterDiagnosticAt, lastRenderErrorAt;
    private string lastDiagnostic = string.Empty;
    private Filter? pendingRenderFailure;
    private string textureDiagnostic = "not-read";
    private string? pendingTextureDiagnostic;
    private DateTime capturedUtc;
    private IReadOnlyList<ViewSingleItemChoice> choices = Array.Empty<ViewSingleItemChoice>();
    private ImageIdentity? imageIdentity;
    private IDalamudTextureWrap? retainedImage;
    private int sceneImageRetirement;
    private Vector2 viewport;
    private Vector2 requestedViewport;
    private long viewportChangedAt;
    internal Vector2 ViewportSize => viewport;

    // Exact supported-build ABI proved from the mesh loop at 0x2829C1..0x2829DA.
    // The caller supplies four arguments and never consumes a return value.
    private delegate void MeshRenderDelegate(ModelRenderer* renderer, ModelRenderer.OnRenderModelParams* parameters,
        ModelResourceHandle* modelResource, uint meshIndex);

    private sealed record MaterialIdentity(nint Material, nint Resource, bool Skin, string Shader = "");
    private sealed record ModelIdentity(nint Model, nint Resource, nint Owner, uint Slot, int Choice,
        MaterialIdentity[] Materials, uint Attributes = 0, uint Shapes = 0);
    private sealed record Filter(InspectOutfitIdentity Player, nint Character, nint[] Owners, ModelIdentity[] Models, int Selected,
        long Deadline, long Revision, long Frame, RenderDiagnostics Diagnostics, string SelectionInfo);
    // All counters belong to one revision. Old workers cannot overwrite a newer
    // revision's acknowledgement, and heartbeat publications share this state.
    private sealed class RenderDiagnostics
    {
        internal long ObservedFrame = -1, SelectedFrame = -1;
        internal long Owned, Matched, Selected, Forwarded;
        internal readonly long[] Rejected = new long[(int)RenderRejection.Count];
        internal Exception? Error;
    }
    private enum RenderRejection
    {
        Expired, Invalidated, PublicationChanged, ModelChanged, MeshUnavailable,
        MaterialBounds, MaterialChanged, Skin, Exception, Count,
    }
    private enum ImageBlocker
    {
        None, NoFilter, Expired, Invalidated, MissingModels, NoSceneSubmission,
        NoSelectedSubmission, SkinFiltered, FrameSettling,
    }
    private readonly record struct ImageIdentity(nint Texture, nint View, Vector2 Actual, Vector2 Allocated);

    private global::XASlave.Localization.UiMessage? statusMessage = global::XASlave.Localization.UiMessage.Key("SE3872DCC0D95A9AE");
    internal global::XASlave.Localization.UiMessage StatusMessage => global::XASlave.Localization.UiMessage.Capture(statusMessage, StatusText);
    internal string LocalizedStatusText => StatusMessage.Display;
    internal string StatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("SE3872DCC0D95A9AE");
    private global::XASlave.Localization.UiMessage? unavailableMessage;
    internal global::XASlave.Localization.UiMessage UnavailableMessage => global::XASlave.Localization.UiMessage.Capture(unavailableMessage, UnavailableReason);
    internal string UnavailableReason { get; private set; } = string.Empty;
    internal string ReadinessDiagnostic { get; private set; } = string.Empty;
    internal Vector2 ImageUvMax { get; private set; } = Vector2.One;
    internal bool WeaponDrawn => scene.WeaponDrawn;
    internal uint? FramerKitId => catalog?.FramerKitId;

    // An empty inventory off-hand slot can still have the second half of a
    // paired main-hand weapon in the captured draw data (daggers, twinblades).
    private bool HasPairedWeapon => source != null && source.Equipment.Any(item => item.Slot == 1 && item.BaseId == 0);

    internal ViewSingleItemPreview()
    {
        NearbyZoneNativeBinding.RequireFramework();
        var sdk = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(AgentInspect).Assembly.Location)));
        if (sdk is not ("A47750CE4BE1910C8783231F398559BCCE43E4A6AE8D255351B3BBA8247D6F9D" or "98795452ED0636E3613850176310AEF1E4E8B85AE47A3E722FF2C325CF2801D2" or "F5A2C68CCECD3E199BDA572050E98902B2E7594066C95DC7826C2D4297CD0564"))
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S3AC296CB69ECFF46"), originalMessageText => new InvalidOperationException(originalMessageText));
        ValidateLayouts();
        var module = Plugin.SigScanner.Module;
        var bytes = File.ReadAllBytes(module.FileName);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != NearbyZoneNativeBinding.SupportedSha256)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SF0B352CBCC662406"), originalMessageText => new InvalidOperationException(originalMessageText));
        using var stream = new MemoryStream(bytes, false);
        using var pe = new PEReader(stream);
        if (pe.PEHeaders.PEHeader?.SizeOfImage != module.ModuleMemorySize)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S94B5DBAC63D01E98"), originalMessageText => new InvalidOperationException(originalMessageText));
        moduleBase = module.BaseAddress;
        var original = pe.GetSectionData(MeshGateRva).GetContent(0, MeshGateLength).ToArray();
        using var hookRegistration = ViewSingleItemRendererHookState.GetSyncRoot().EnterScope();
        var savedEntry = ViewSingleItemRendererHookState.ReadSavedEntry(moduleBase + MeshGateRva, MeshGateLength);
        var loaded = NearbyZoneNativeBinding.Read(moduleBase + MeshGateRva, MeshGateLength);
        if (!ViewSingleItemRendererHookState.MatchesSupportedCode(original, loaded, savedEntry))
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Format("S18116930FDB1719F" , ViewSingleItemRendererDiagnostics.DescribeBytes(MeshGateRva, original, loaded), ViewSingleItemRendererDiagnostics.DescribeOwners(moduleBase + MeshGateRva, MeshGateLength)), originalMessageText => new InvalidOperationException(originalMessageText));
        meshHook = Plugin.GameInterop.HookFromAddress<MeshRenderDelegate>(moduleBase + MeshGateRva, RenderMesh);
        try
        {
            meshHook.Enable();
            installedBody = NearbyZoneNativeBinding.Read(moduleBase + MeshGateRva, MeshGateLength);
            scene = new ViewSingleItemScene(moduleBase, pe, RetireSceneImage);
        }
        catch { meshHook.Dispose(); throw; }
    }

    private static void ValidateLayouts()
    {
        if (sizeof(AgentInspect) != 0x940 || sizeof(CharaView) != 0x318 || sizeof(Model) != 0x180 || sizeof(Human) != 0xCD0
            || sizeof(DrawDataContainer) != 0x268 || sizeof(DrawObjectData) != 0x70
            || sizeof(SceneObject) != 0x80
            || sizeof(SceneCamera) != 0x100 || sizeof(RenderCamera) != 0x290)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SABACD1547BA6E00C"), originalMessageText => new InvalidOperationException(originalMessageText));
        RequireOffset<AgentInspect>(nameof(AgentInspect.CharaView), 0x628);
        RequireOffset<CharaView>(nameof(CharaView.State), 0x8);
        RequireOffset<CharaView>(nameof(CharaView.ClientObjectId), 0xC);
        RequireOffset<CharaView>(nameof(CharaView.ClientObjectIndex), 0x10);
        RequireOffset<CharaView>(nameof(CharaView.Agent), 0x30);
        RequireOffset<CharaView>(nameof(CharaView.CharacterDataCopied), 0x309);
        RequireOffset<CharaView>(nameof(CharaView.CharacterLoaded), 0x30A);
        RequireOffset<CharaView>(nameof(CharaView.Camera), 0x20);
        RequireOffset<CharaView>(nameof(CharaView.ZoomRatio), 0xEC);
        RequireOffset<SceneCamera>(nameof(SceneCamera.RenderCamera), 0xE0);
        RequireOffset<RenderCamera>(nameof(RenderCamera.OrthoHeight), 0x1FC);
        RequireOffset<RenderCamera>(nameof(RenderCamera.IsOrtho), 0x200);
        RequireOffset<NativeCharacter>(nameof(NativeCharacter.DrawData), 0x6F8);
        RequireOffset<NativeCharacter>(nameof(NativeCharacter.OrnamentData), 0x960);
        RequireOffset<OrnamentContainer>(nameof(OrnamentContainer.OwnerObject), 0x8);
        RequireOffset<OrnamentContainer>(nameof(OrnamentContainer.OrnamentObject), 0x10);
        RequireOffset<OrnamentContainer>(nameof(OrnamentContainer.OrnamentId), 0x18);
        RequireOffset<DrawObjectData>(nameof(DrawObjectData.Weapon), 0x8);
        RequireOffset<CharacterBase>(nameof(CharacterBase.Models), 0xA8);
        RequireOffset<SceneObject>(nameof(SceneObject.ParentObject), 0x18);
        RequireOffset<SceneObject>(nameof(SceneObject.NextSiblingObject), 0x28);
        RequireOffset<SceneObject>(nameof(SceneObject.ChildObject), 0x30);
        RequireOffset<Model>(nameof(Model.SlotIndex), 0x118);
        RequireOffset<Model>(nameof(Model.EnabledAttributeIndexMask), 0xAC);
        RequireOffset<Model>(nameof(Model.EnabledShapeKeyIndexMask), 0xC8);
        RequireOffset<Texture>(nameof(Texture.D3D11ShaderResourceView), 0x70);
    }

    private static void RequireOffset<T>(string field, int expected) where T : struct
    {
        // Nested ClientStructs layouts need metadata checks, not marshaling.
        var type = typeof(T);
        var offset = type.GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetCustomAttribute<FieldOffsetAttribute>();
        if (type.StructLayoutAttribute?.Value != LayoutKind.Explicit || offset == null || offset.Value != expected)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Format("S2BAD4969167D3CDF" , type.Name, field), originalMessageText => new InvalidOperationException(originalMessageText));
    }

    internal void Validate()
    {
        NearbyZoneNativeBinding.RequireFramework();
        ObjectDisposedException.ThrowIf(disposed, this);
        DrainSceneImageRetirement();
        if (!meshHook.IsEnabled || !ViewSingleItemScene.MatchesNativeBytes(moduleBase + MeshGateRva, installedBody))
        {
            Clear();
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SF8EBB11384EF76AB"), originalMessageText => new InvalidOperationException(originalMessageText));
        }
        scene.Validate();
    }

    private NativeCharacter* ResolveInspectionCharacter(InspectOutfitSnapshot snapshot)
    {
        UnavailableReason = ReadinessDiagnostic = string.Empty;
        var host = InspectOutfitControls.Resolve(snapshot.Host.Generation);
        if (host != snapshot.Host || !Plugin.ClientState.IsLoggedIn || !Plugin.PlayerState.IsLoaded)
            return Unavailable(global::XASlave.Localization.UiMessage.Key("S79436927C71DF4C0"), "stage=preview-session");
        var state = UIState.Instance();
        NearbyZoneNativeBinding.RequireReadable((nint)state, sizeof(UIState));
        var inspect = &state->Inspect;
        if (inspect->IsInspectRequested || new InspectOutfitIdentity(inspect->EntityId, inspect->WorldId,
                inspect->NameString, inspect->Type) != snapshot.Identity)
            return Unavailable(global::XASlave.Localization.UiMessage.Key("SAF11D9909F7DA243"), "stage=preview-identity");
        var agent = AgentInspect.Instance();
        NearbyZoneNativeBinding.RequireReadable((nint)agent, sizeof(AgentInspect));
        var active = agent->IsAgentActive();
        var buddy = agent->IsBuddyInspect;
        var addonId = agent->AddonId;
        var agentMatches = agent->CharaView.Agent == (AgentInterface*)agent;
        // The supported Render implementation clears both flags on completion;
        // CharacterLoaded is a pending model-update flag, not a ready flag.
        var appearancePending = agent->CharaView.CharacterDataCopied;
        var modelPending = agent->CharaView.CharacterLoaded;
        var viewState = agent->CharaView.State;
        var objectIndex = agent->CharaView.ClientObjectIndex;
        var objectId = agent->CharaView.ClientObjectId;
        var diagnostic = $"stage=preview-character; active={active}; buddy={buddy}; addon={addonId}; expectedAddon={snapshot.Host.Id}; agentMatch={agentMatches}; appearancePending={appearancePending}; modelPending={modelPending}; state={viewState}; index={objectIndex}; id={objectId}";
        if (!active || buddy || addonId != snapshot.Host.Id || !agentMatches)
            return Unavailable(global::XASlave.Localization.UiMessage.Key("S3D03980BD4AA21F9"), diagnostic);
        if (appearancePending || modelPending)
            return Unavailable(global::XASlave.Localization.UiMessage.Key("S0D425D87C38E70DC"), diagnostic);
        if (viewState != 6)
            return Unavailable(global::XASlave.Localization.UiMessage.Key("SAE638AD04887D0A3"), diagnostic);
        if (!IsInspectPreviewOwner(objectIndex, objectId))
            return Unavailable(global::XASlave.Localization.UiMessage.Key("SF9F27A661DF12FB3"), diagnostic);
        var character = agent->CharaView.GetCharacter();
        if (character == null)
            return Unavailable(global::XASlave.Localization.UiMessage.Key("S384C6C7F597DD05A"), diagnostic);
        NearbyZoneNativeBinding.RequireReadable((nint)character, sizeof(NativeCharacter));
        // The client-object manager must independently identify the preview clone.
        var manager = ClientObjectManager.Instance();
        NearbyZoneNativeBinding.RequireReadable((nint)manager, sizeof(ClientObjectManager));
        if (manager->GetObjectByIndex(checked((ushort)agent->CharaView.ClientObjectId)) != character)
            return Unavailable(global::XASlave.Localization.UiMessage.Key("SFD9DC018092A27ED"), diagnostic + "; managerMatch=false");
        var draw = (CharacterBase*)character->DrawObject;
        if (draw == null)
            return Unavailable(global::XASlave.Localization.UiMessage.Key("SCBAE78F8BA1F8BAF"), diagnostic + "; drawObject=false");
        NearbyZoneNativeBinding.RequireReadable((nint)draw, sizeof(CharacterBase));
        var modelType = draw->GetModelType();
        if (modelType != CharacterBase.ModelType.Human)
            return Unavailable(global::XASlave.Localization.UiMessage.Key("S70A64EDF9CEF3C28"), diagnostic + "; modelType=" + modelType);
        return character;
    }

    // The exact supported Initialize uses index + 0xF0; Inspect owns index 1.
    private static bool IsInspectPreviewOwner(uint objectIndex, uint objectId)
        => objectIndex == 1 && objectId == objectIndex + 0xF0;

    private NativeCharacter* Unavailable(global::XASlave.Localization.UiMessage reason, string diagnostic)
    {
        UnavailableReason = global::XASlave.Localization.UiMessage.Store(ref unavailableMessage, reason); ReadinessDiagnostic = diagnostic;
        return null;
    }

    internal IReadOnlyList<ViewSingleItemChoice> Prepare(InspectOutfitSnapshot snapshot)
    {
        Clear(); Validate();
        var character = ResolveInspectionCharacter(snapshot);
        if (character == null) return Array.Empty<ViewSingleItemChoice>();
        var equipped = InspectOutfitPlan.Create(snapshot.Equipment).ToDictionary(item => (int)item.Slot);
        var result = new List<ViewSingleItemChoice>();
        foreach (var slot in InspectOutfitPlan.Slots)
        {
            if (!equipped.TryGetValue(slot, out var item))
            {
                result.Add(ViewSingleItemChoice.Create(slot, global::XASlave.Localization.UiMessage.Format("S665654451B8998BC" , SlotLabel(slot)), 0, global::XASlave.Localization.UiMessage.Key("S50C21EA3F6F55ED3")));
                continue;
            }
            var row = Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(item.Appearance.Item);
            var name = row is { } itemRow ? global::XASlave.Localization.UiMessage.Raw(itemRow.Name.ToString()) : global::XASlave.Localization.UiMessage.Format("SFAD8B09F2BDD2797" , item.Appearance.Item);
            result.Add(ViewSingleItemChoice.Create(item.Slot, global::XASlave.Localization.UiMessage.Format("SFB62FE2AE6B2AE6C" , SlotLabel(item.Slot), name), item.Appearance.Item));
        }
        result.Add(ViewSingleItemChoice.Create(100, global::XASlave.Localization.UiMessage.Key("SD640CF0CFA2C6004"), 0));
        scene.Capture(character);
        var appearance = scene.ExportAppearance();
        var glasses = BitConverter.ToUInt16(appearance, 0x1A);
        var glasses2 = BitConverter.ToUInt16(appearance, 0x1C);
        result.Add(ViewSingleItemChoice.Create(101, (glasses != 0 || glasses2 != 0 ? global::XASlave.Localization.UiMessage.Format("S984BE847B1B87AED" , glasses, glasses2) : global::XASlave.Localization.UiMessage.Key("SE9CC45CDF977F88A")), 0, (glasses == 0 && glasses2 == 0 ? global::XASlave.Localization.UiMessage.Key("SA749BAC66915EA70") : global::XASlave.Localization.UiMessage.Raw(string.Empty))));
        ornament = ViewSingleItemService.CaptureOrnamentMessage(snapshot.Identity, out var ornamentUnavailable);
        result.Add(OrnamentChoice(ornament, ornamentUnavailable));
        minion = ViewSingleItemCompanions.CaptureMessage(snapshot.Identity, false, out var minionUnavailable);
        mount = ViewSingleItemCompanions.CaptureMessage(snapshot.Identity, true, out var mountUnavailable);
        result.Add(ViewSingleItemCompanions.Choice(103, minion, minionUnavailable));
        result.Add(ViewSingleItemCompanions.Choice(104, mount, mountUnavailable));
        capturedUtc = DateTime.UtcNow;
        source = snapshot; choices = result.AsReadOnly();
        return choices;
    }

    internal ViewSingleItemCapture ExportCapture()
    {
        Validate();
        if (source == null) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S1BE83053D7E1622E"), originalMessageText => new InvalidOperationException(originalMessageText));
        var appearance = scene.ExportAppearance();
        var schema = catalog != null || choices.Any(choice => choice.Key == 103) ? 3 : 2;
        return new(schema, NearbyZoneNativeBinding.SupportedSha256, ViewSingleItemCapture.SupportedLayout,
            source.Identity.Name, source.Identity.World, capturedUtc,
            (InspectOutfitItem[])source.Equipment.Clone(), choices.ToArray(), appearance,
            Convert.ToHexString(SHA256.HashData(appearance)), ornament, ornament?.Hash(),
            minion, minion?.Hash(), mount, mount?.Hash(), catalog, catalog?.Hash());
    }

    internal ViewSingleItemCapture PrepareCatalog(ViewSingleItemCatalogEntry entry, NativeCharacter* reference)
    {
        Clear(); Validate();
        // Reuse the existing capture/geometry pipeline. The catalog constructor
        // keeps only customization and the requested item, never this outfit.
        scene.Capture(reference);
        var capture = ViewSingleItemShopSource.CreateCapture(entry, scene.ExportAppearance());
        return capture;
    }

    internal IReadOnlyList<ViewSingleItemChoice> Restore(ViewSingleItemCapture capture, InspectOutfitSnapshot snapshot)
    {
        capture.Validate(); Clear(); Validate();
        scene.Restore(capture);
        ornament = capture.Ornament;
        minion = capture.Minion; mount = capture.Mount; catalog = capture.Catalog;
        source = snapshot; capturedUtc = capture.CapturedUtc;
        var restored = (ViewSingleItemChoice[])capture.Choices.Clone();
        if (catalog == null)
            for (var index = 0; index < restored.Length; index++)
                restored[index] = RestoreChoicePresentation(restored[index], capture.Appearance);
        var accessoryIndex = Array.FindIndex(restored, choice => choice.Key == 102);
        if (accessoryIndex >= 0 && capture.Schema == 1)
        {
            restored[accessoryIndex] = ViewSingleItemChoice.Create(102, global::XASlave.Localization.UiMessage.Key("S2D47ACAF6F69836B"), 0, global::XASlave.Localization.UiMessage.Key("SFCD5E98F304A74C4"));
        }
        else if (accessoryIndex >= 0) restored[accessoryIndex] = OrnamentChoice(ornament, restored[accessoryIndex].UnavailablePresentation);
        // Re-check saved row/model relationships before exposing native models.
        for (var index = 0; index < restored.Length; index++)
        {
            var key = restored[index].Key;
            if (key is not (103 or 104)) continue;
            var value = key == 103 ? minion : mount;
            var updated = ViewSingleItemCompanions.Choice(key, value, restored[index].UnavailablePresentation);
            restored[index] = catalog == null ? updated : updated with { ItemId = catalog.ItemId };
        }
        if (catalog != null)
        {
            if (!ViewSingleItemShopSource.TryDescribeMessage(catalog.ItemId, out var current, out var reason)
                || current.Key != catalog.Key || current.FramerKitId != catalog.FramerKitId
                || (catalog.Key is 103 or 104 && !Equals(current.Companion, catalog.Key == 103 ? minion : mount)))
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Format("SB779B7865E5CBE42" , reason), originalMessageText => new InvalidDataException(originalMessageText));
            if (!capture.Appearance.AsSpan().SequenceEqual(ViewSingleItemShopSource.CreateAppearance(current, capture.Appearance)))
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SB52DAA82CCFCF2DC"), originalMessageText => new InvalidDataException(originalMessageText));
        }
        choices = Array.AsReadOnly(restored);
        return choices;
    }

    // Saved choices retain their original text and game names. Recover only the
    // authored wrapper whose slot/schema identifies its origin; unknown text stays raw.
    private static ViewSingleItemChoice RestoreChoicePresentation(ViewSingleItemChoice choice, byte[] appearance)
    {
        global::XASlave.Localization.UiMessage? label = null, unavailable = null;
        if (Array.Exists(InspectOutfitPlan.Slots, slot => slot == choice.Key))
        {
            var slot = SlotLabel(choice.Key);
            if (choice.ItemId == 0)
            {
                label = global::XASlave.Localization.UiMessage.Format("S665654451B8998BC" , slot);
                unavailable = global::XASlave.Localization.UiMessage.Key("S50C21EA3F6F55ED3");
            }
            else
            {
                var prefix = slot.Original + ": ";
                if (choice.Label.StartsWith(prefix, StringComparison.Ordinal))
                    label = global::XASlave.Localization.UiMessage.Format("SFB62FE2AE6B2AE6C" , slot, choice.Label[prefix.Length..]);
            }
        }
        else if (choice.Key == 100) label = global::XASlave.Localization.UiMessage.Key("SD640CF0CFA2C6004");
        else if (choice.Key == 101)
        {
            var glasses = BitConverter.ToUInt16(appearance, 0x1A);
            var glasses2 = BitConverter.ToUInt16(appearance, 0x1C);
            label = glasses != 0 || glasses2 != 0 ? global::XASlave.Localization.UiMessage.Format("S984BE847B1B87AED" , glasses, glasses2) : global::XASlave.Localization.UiMessage.Key("SE9CC45CDF977F88A");
            if (glasses == 0 && glasses2 == 0) unavailable = global::XASlave.Localization.UiMessage.Key("SA749BAC66915EA70");
        }
        return choice with
        {
            LabelMessage = label != null && label.Original == choice.Label ? label : choice.LabelMessage,
            UnavailableMessage = unavailable != null && unavailable.Original == choice.UnavailableReason ? unavailable : choice.UnavailableMessage,
        };
    }

    private static ViewSingleItemChoice OrnamentChoice(ViewSingleItemOrnament? value, global::XASlave.Localization.UiMessage unavailable)
    {
        if (value == null) return ViewSingleItemChoice.Create(102, global::XASlave.Localization.UiMessage.Key("S2D47ACAF6F69836B"), 0, (string.IsNullOrEmpty(unavailable.Original) ? global::XASlave.Localization.UiMessage.Key("S94A2CE8862AD3D29") : unavailable));
        if (value.Id == 0) return ViewSingleItemChoice.Create(102, global::XASlave.Localization.UiMessage.Key("S65C578A428F5C224"), 0, global::XASlave.Localization.UiMessage.Key("S80C3514512962647"));
        var row = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Ornament>().GetRowOrDefault(value.Id);
        var name = row?.Singular.ToString();
        return ViewSingleItemChoice.Create(102, global::XASlave.Localization.UiMessage.Format("S38BB8BEC43FFED77" , ((string.IsNullOrWhiteSpace(name) ? global::XASlave.Localization.UiMessage.Format("S20BEBB1632B1D76A" , value.Id) : global::XASlave.Localization.UiMessage.Raw(name)))), 0);
    }

    internal bool Select(int key, InspectOutfitSnapshot snapshot)
    {
        Validate();
        if (source == null || !source.SameSource(snapshot) || !choices.Any(value => value.Key == key)) return false;
        ResetRecordedAnimation();
        revision++; revisionStarted = Environment.TickCount64;
        selection = key;
        Volatile.Write(ref filter, null);
        if (key is not (105 or 106)) scene.Select(key, HasPairedWeapon, ornament, key == 103 ? minion : key == 104 ? mount : null);
        // Every category click starts with the default framing, including a
        // second click on the selected category or a reused clothing scene.
        scene.ResetCamera();
        DrainSceneImageRetirement();
        lastDiagnostic = string.Empty; lastDiagnosticAt = lastFilterDiagnosticAt = 0;
        Plugin.Log.Information("[View Item] Selected {Selection}; revision={Revision}; frame={Frame}.",
            DescribeSelection(), revision, frame);
        if (key == 105)
        {
            StatusText = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Key("S8DD186B55BC92CC9"));
            return true;
        }
        if (key == 106)
        {
            if (catalog == null || !ViewSingleItemFurnitureAssets.TryDescribe(catalog.ItemId, out var furnishing))
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S0F4EF07904685BB6"), originalMessageText => new InvalidDataException(originalMessageText));
            Furniture.Load(furnishing);
            StatusText = global::XASlave.Localization.UiMessage.Store(ref statusMessage, Furniture.StatusMessage);
            return true;
        }
        Refresh(snapshot);
        StatusText = global::XASlave.Localization.UiMessage.Store(ref statusMessage, choices.First(value => value.Key == key).UnavailablePresentation);
        if (string.IsNullOrEmpty(StatusText)) StatusText = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Key("SA333D4BDF30300CD"));
        return true;
    }

    internal void SetWeaponDrawn(bool drawn)
    {
        Validate();
        if (source == null || selection is not (0 or 1) || scene.WeaponDrawn == drawn) return;
        ResetRecordedAnimation();
        scene.SetWeaponDrawn(drawn);
        DrainSceneImageRetirement();
        lastDiagnostic = string.Empty; lastDiagnosticAt = lastFilterDiagnosticAt = 0;
        StatusText = global::XASlave.Localization.UiMessage.Store(ref statusMessage, (drawn ? global::XASlave.Localization.UiMessage.Key("S1AE44151FC56CA2D") : global::XASlave.Localization.UiMessage.Key("S74D9A29A000D1FB5")));
        Plugin.Log.Information("[View Item] Weapon state: {Selection}; drawn={Drawn}; revision={Revision}.",
            DescribeSelection(), drawn, revision);
    }

    internal void Tick(long frameworkFrame)
    {
        Validate(); frame = frameworkFrame;
        if (source == null || selection == 105) return;
        if (selection == 106)
        {
            Furniture.Tick();
            StatusText = global::XASlave.Localization.UiMessage.Store(ref statusMessage, Furniture.StatusMessage);
            return;
        }
        scene.Tick(frameworkFrame);
        DrainSceneImageRetirement();
        UpdateRecordedAnimation();
        if (selection != null) Refresh(source);
        DrainRenderDiagnostics();
    }

    internal bool Refresh(InspectOutfitSnapshot snapshot)
    {
        lock (scene.SyncRoot)
        {
            Validate();
            if (source == null || !source.SameSource(snapshot) || selection == null)
            { Volatile.Write(ref filter, null); return false; }
            var unavailable = choices.FirstOrDefault(value => value.Key == selection)?.UnavailableReason;
            if (!string.IsNullOrEmpty(unavailable))
            { Volatile.Write(ref filter, null); StatusText = global::XASlave.Localization.UiMessage.Store(ref statusMessage, choices.First(value => value.Key == selection).UnavailablePresentation); return false; }
            var character = scene.GetCharacter();
            if (character == null)
            { Volatile.Write(ref filter, null); StatusText = global::XASlave.Localization.UiMessage.Store(ref statusMessage, scene.StatusTextMessage); return false; }
            var models = new List<ModelIdentity>();
            var owners = new List<nint>();
            var descendants = new HashSet<nint>();
            var roots = new List<(nint Draw, int Choice)>();
            owners.Add((nint)character->DrawObject);
            CaptureModels((CharacterBase*)character->DrawObject, scene.IsStandaloneModel ? selection.Value : -1, models);
            for (var index = 0; index < 3; index++)
            {
                // Weapon is the owned scene object; DrawData.DrawObject is only its attachment cache.
                var weapon = character->DrawData.WeaponData[index].Weapon;
                if (weapon == null) continue;
                NearbyZoneNativeBinding.RequireReadable((nint)weapon, sizeof(CharacterBase));
                if (((CharacterBase*)weapon)->GetModelType() != CharacterBase.ModelType.Weapon) continue;
                // Keep paired parts and auxiliary main-hand geometry together;
                // an independently equipped shield/tool remains its own choice.
                var weaponChoice = index == 1 && !HasPairedWeapon ? 1 : 0;
                owners.Add((nint)weapon);
                CaptureModels((CharacterBase*)weapon, weaponChoice, models);
                roots.Add(((nint)weapon, weaponChoice));
            }
            // Capture descendant owners even when their model arrays are still empty.
            // Weapons are classified first so attachments keep their weapon choice.
            foreach (var root in roots) CaptureChildren((SceneObject*)root.Draw, root.Choice, owners, models, descendants);
            CaptureChildren((SceneObject*)character->DrawObject, scene.IsStandaloneModel ? selection.Value : -2, owners, models, descendants);
            var previous = Volatile.Read(ref filter);
            var change = previous == null ? "scene-ready"
                : previous.Selected != selection.Value ? "selection"
                : Environment.TickCount64 > previous.Deadline ? "heartbeat-expired"
                : Interlocked.Read(ref invalidRevision) == previous.Revision ? "render-invalidated"
                : (nint)character != previous.Character || !previous.Owners.SequenceEqual(owners) ? "owner-changed"
                : !SameModels(previous.Models, models) ? "model-or-material-changed" : string.Empty;
            if (change.Length != 0)
            {
                revision++;
                revisionStarted = Environment.TickCount64;
            }
            // Immutable value identities cross to render workers; none are dereferenced later.
            var fresh = new Filter(snapshot.Identity, (nint)character, owners.ToArray(), models.ToArray(), selection.Value,
                Environment.TickCount64 + 1000, revision, frame,
                previous != null && previous.Revision == revision ? previous.Diagnostics : new RenderDiagnostics(), DescribeSelection());
            Volatile.Write(ref filter, fresh);
            if (change.Length != 0) LogFilterChange(previous, fresh, change);
            return true;
        }
    }

    private static void CaptureChildren(SceneObject* parent, int choice, List<nint> owners, List<ModelIdentity> models,
        HashSet<nint> descendants, int depth = 0)
    {
        if (depth > 8 || owners.Count > 64) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S4DC83950EB0C04DF"), originalMessageText => new InvalidOperationException(originalMessageText));
        NearbyZoneNativeBinding.RequireReadable((nint)parent, sizeof(SceneObject));
        var first = parent->ChildObject;
        var child = first;
        var visited = new HashSet<nint>();
        while (child != null)
        {
            if (visited.Count >= 64 || !visited.Add((nint)child))
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S25B3AE5D69D3AEB9"), originalMessageText => new InvalidOperationException(originalMessageText));
            NearbyZoneNativeBinding.RequireReadable((nint)child, sizeof(SceneObject));
            if (child->ParentObject != parent)
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SEB1C97BCB7CFF165"), originalMessageText => new InvalidOperationException(originalMessageText));
            if (!owners.Contains((nint)child))
            {
                if (descendants.Count >= 128 || !descendants.Add((nint)child))
                    throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SB0796512CF9EDBCD"), originalMessageText => new InvalidOperationException(originalMessageText));
                if (child->GetObjectType() == FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType.CharacterBase)
                {
                    owners.Add((nint)child);
                    CaptureModels((CharacterBase*)child, choice, models);
                }
                CaptureChildren(child, choice, owners, models, descendants, depth + 1);
            }
            child = child->NextSiblingObject;
            if (child == first) break; // Native siblings form a circular list.
        }
    }

    private static bool SameModels(ModelIdentity[] previous, List<ModelIdentity> current)
    {
        if (previous.Length != current.Count) return false;
        for (var index = 0; index < previous.Length; index++)
        {
            var old = previous[index]; var fresh = current[index];
            if (old.Model != fresh.Model || old.Resource != fresh.Resource || old.Owner != fresh.Owner
                || old.Slot != fresh.Slot || old.Choice != fresh.Choice || !old.Materials.SequenceEqual(fresh.Materials))
                return false;
        }
        return true;
    }

    private static void CaptureModels(CharacterBase* draw, int weaponChoice, List<ModelIdentity> target)
    {
        NearbyZoneNativeBinding.RequireReadable((nint)draw, sizeof(CharacterBase));
        if (draw->SlotCount < 0 || draw->SlotCount > 32)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S40051C450F7CBA56"), originalMessageText => new InvalidOperationException(originalMessageText));
        if (draw->SlotCount == 0 || draw->Models == null) return;
        NearbyZoneNativeBinding.RequireReadable((nint)draw->Models, checked(draw->SlotCount * sizeof(nint)));
        for (var index = 0; index < draw->SlotCount; index++)
        {
            var model = draw->Models[index];
            if (model == null) continue;
            NearbyZoneNativeBinding.RequireReadable((nint)model, sizeof(Model));
            if (model->ModelResourceHandle == null || model->RenderModelCallback == null) continue;
            NearbyZoneNativeBinding.RequireReadable((nint)model->RenderModelCallback, sizeof(ModelRenderer.Callback));
            if ((nint)model->RenderModelCallback->ThisArg != (nint)draw || model->SlotIndex != (uint)index)
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S9F517FB0BB6AD20D"), originalMessageText => new InvalidOperationException(originalMessageText));
            if (model->MaterialCount < 0 || model->MaterialCount > 128 || model->Materials == null)
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SDE176A44868F47AB"), originalMessageText => new InvalidOperationException(originalMessageText));
            var materials = new MaterialIdentity[model->MaterialCount];
            if (materials.Length != 0) NearbyZoneNativeBinding.RequireReadable((nint)model->Materials,
                checked(materials.Length * sizeof(nint)));
            for (var materialIndex = 0; materialIndex < materials.Length; materialIndex++)
            {
                var material = model->Materials[materialIndex];
                if (material == null) { materials[materialIndex] = new(0, 0, false); continue; }
                NearbyZoneNativeBinding.RequireReadable((nint)material, sizeof(Material));
                var resource = material->MaterialResourceHandle;
                NearbyZoneNativeBinding.RequireReadable((nint)resource, sizeof(MaterialResourceHandle));
                var shader = ReadShaderName(resource);
                materials[materialIndex] = new((nint)material, (nint)resource, IsSkinShader(shader), shader);
            }
            var choice = weaponChoice == -1 ? ModelChoice(index) : weaponChoice;
            target.Add(new((nint)model, (nint)model->ModelResourceHandle, (nint)draw,
                model->SlotIndex, choice, materials, model->EnabledAttributeIndexMask, model->EnabledShapeKeyIndexMask));
        }
    }

    // characterlegacy.shpk is a general equipment material shader, not evidence of skin.
    // Keep its clothing/weapon meshes while suppressing separate body materials.
    private static bool IsSkinShader(string shader)
        => shader is "skin.shpk" or "charactertattoo.shpk" or "characterocclusion.shpk";

    private static string ReadShaderName(MaterialResourceHandle* material)
    {
        if (material->Strings == null || material->ShpkNameOffset >= material->TotalDataSize)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S9C8A6B1495FCD95D"), originalMessageText => new InvalidOperationException(originalMessageText));
        var pointer = material->Strings + material->ShpkNameOffset;
        var length = Math.Min(128, material->TotalDataSize - material->ShpkNameOffset);
        NearbyZoneNativeBinding.RequireReadable((nint)pointer, length);
        var data = new ReadOnlySpan<byte>(pointer, length);
        var end = data.IndexOf((byte)0);
        if (end < 0) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SC866032C1E367A1F"), originalMessageText => new InvalidOperationException(originalMessageText));
        return Encoding.UTF8.GetString(data[..end]);
    }

    private void RenderMesh(ModelRenderer* renderer, ModelRenderer.OnRenderModelParams* parameters,
        ModelResourceHandle* resource, uint meshIndex)
    {
        var current = Volatile.Read(ref filter);
        var owned = false;
        var selected = false;
        try
        {
            if (current != null && parameters != null
                && parameters->Model != null && resource != null)
            {
                var model = parameters->Model;
                var callback = model->RenderModelCallback;
                var owner = callback == null ? 0 : (nint)callback->ThisArg;
                owned = Array.IndexOf(current.Owners, owner) >= 0;
                if (owned) Interlocked.Increment(ref current.Diagnostics.Owned);
                var capturedModel = false;
                foreach (var identity in current.Models)
                {
                    if (identity.Model == (nint)model) capturedModel = true;
                }
                if (!owned)
                {
                    if (!capturedModel) goto Forward;
                    Reject(current, RenderRejection.ModelChanged);
                    InvalidateFilter(current);
                    // A reused address with a different live owner is no longer our preview.
                    if (callback != null) goto Forward;
                    return;
                }
                // Never forward the rest of our scene after a mismatch or expiry.
                // Keep owner containment until the framework supplies a fresh filter.
                var active = Volatile.Read(ref filter);
                if (active == null || active.Revision != current.Revision)
                { Reject(current, RenderRejection.PublicationChanged); return; }
                if (Environment.TickCount64 > active.Deadline)
                { Reject(current, RenderRejection.Expired); return; }
                if (Interlocked.Read(ref invalidRevision) == current.Revision)
                { Reject(current, RenderRejection.Invalidated); return; }
                if (callback == null)
                { Reject(current, RenderRejection.ModelChanged); InvalidateFilter(current); return; }
                var matched = false;
                foreach (var identity in current.Models)
                {
                    if (identity.Model != (nint)model || identity.Resource != (nint)resource) continue;
                    if (model->ModelResourceHandle != resource || model->SlotIndex != identity.Slot
                        || model->RenderModelCallback == null || (nint)model->RenderModelCallback->ThisArg != identity.Owner)
                        break;
                    matched = true;
                    Interlocked.Increment(ref current.Diagnostics.Matched);
                    if (!IsCurrentFilter(current))
                    { Reject(current, RenderRejection.PublicationChanged); return; }
                    var observation = Volatile.Read(ref filter);
                    if (observation == null || observation.Revision != current.Revision) return;
                    Interlocked.CompareExchange(ref current.Diagnostics.ObservedFrame, observation.Frame, -1);
                    if (identity.Choice != current.Selected) return;
                    Interlocked.Increment(ref current.Diagnostics.Selected);
                    // This mesh table and material-index lookup are proved from the exact guarded gate.
                    if (meshIndex > ushort.MaxValue)
                    { Reject(current, RenderRejection.MeshUnavailable); return; }
                    var meshTable = (byte**)((byte*)resource + 0xE8);
                    var meshes = *meshTable;
                    if (meshes == null)
                    { Reject(current, RenderRejection.MeshUnavailable); return; }
                    var meshMaterial = (ushort*)(meshes + checked((int)meshIndex * 0x24) + 8);
                    var materialIndex = *meshMaterial;
                    if (materialIndex >= identity.Materials.Length || model->Materials == null
                        || materialIndex >= model->MaterialCount)
                    { Reject(current, RenderRejection.MaterialBounds); return; }
                    var material = model->Materials[materialIndex];
                    var saved = identity.Materials[materialIndex];
                    if (material == null || saved.Material != (nint)material
                        || saved.Resource != (nint)material->MaterialResourceHandle)
                    { Reject(current, RenderRejection.MaterialChanged); InvalidateFilter(current); return; }
                    // Accessory scenes contain only the admitted nonhuman item
                    // root; all its materials belong to that single item.
                    if (saved.Skin && current.Selected is not (102 or 103 or 104))
                    { Reject(current, RenderRejection.Skin); return; }
                    // A hidden weapon can have loaded models without submitting any mesh.
                    // Other preview models may confirm suppression, but not this selection.
                    selected = true;
                    break;
                }
                if (!matched)
                { Reject(current, RenderRejection.ModelChanged); InvalidateFilter(current); return; }
                if (!IsCurrentFilter(current))
                { Reject(current, RenderRejection.PublicationChanged); return; }
            }
        }
        catch (Exception error)
        {
            // Retire image admission while retaining containment of this owned scene.
            if (current != null)
            {
                Interlocked.CompareExchange(ref current.Diagnostics.Error, error, null);
                Reject(current, RenderRejection.Exception);
                InvalidateFilter(current);
            }
            if (owned) return;
        }
        Forward:
        try { meshHook.OriginalDisposeSafe(renderer, parameters, resource, meshIndex); }
        catch (Exception error) when (owned && current != null)
        {
            Interlocked.CompareExchange(ref current.Diagnostics.Error, error, null);
            Reject(current, RenderRejection.Exception);
            InvalidateFilter(current);
            return;
        }
        if (selected && current != null && IsCurrentFilter(current))
        {
            var observation = Volatile.Read(ref filter);
            if (observation == null || observation.Revision != current.Revision) return;
            Interlocked.Increment(ref current.Diagnostics.Forwarded);
            Interlocked.CompareExchange(ref current.Diagnostics.SelectedFrame, observation.Frame, -1);
        }
    }

    private bool IsCurrentFilter(Filter current)
    {
        var active = Volatile.Read(ref filter);
        // Refresh publishes a new heartbeat object even when all identities stay
        // unchanged. Revision, rather than object identity, owns admission.
        return active != null && active.Revision == current.Revision
            && Environment.TickCount64 <= active.Deadline
            && Interlocked.Read(ref invalidRevision) != current.Revision;
    }

    private void Reject(Filter current, RenderRejection reason)
    {
        Interlocked.Increment(ref current.Diagnostics.Rejected[(int)reason]);
        // Retain at most one revision's value identities/atomic counters until
        // the framework can log it, even if Refresh replaces it meanwhile.
        if (reason != RenderRejection.Skin && reason != RenderRejection.Invalidated)
            Interlocked.CompareExchange(ref pendingRenderFailure, current, null);
    }

    private void InvalidateFilter(Filter current)
    {
        // Revision numbers are monotonic. An old worker must not invalidate or
        // restore admission for a newer revision that has already replaced it.
        long previous;
        do
        {
            previous = Interlocked.Read(ref invalidRevision);
            if (previous >= current.Revision) return;
        }
        while (Interlocked.CompareExchange(ref invalidRevision, current.Revision, previous) != previous);
    }

    private ImageBlocker GetImageBlocker(Filter? current)
    {
        if (current == null) return ImageBlocker.NoFilter;
        if (Interlocked.Read(ref invalidRevision) == current.Revision) return ImageBlocker.Invalidated;
        if (Environment.TickCount64 > current.Deadline) return ImageBlocker.Expired;
        if (!Array.Exists(current.Models, model => model.Choice == current.Selected)) return ImageBlocker.MissingModels;
        var observed = Interlocked.Read(ref current.Diagnostics.ObservedFrame);
        if (observed < 0) return ImageBlocker.NoSceneSubmission;
        var selectedFrame = Interlocked.Read(ref current.Diagnostics.SelectedFrame);
        if (selectedFrame < 0)
        {
            if (Interlocked.Read(ref current.Diagnostics.Selected) > 0
                && Interlocked.Read(ref current.Diagnostics.Rejected[(int)RenderRejection.Skin])
                    == Interlocked.Read(ref current.Diagnostics.Selected)) return ImageBlocker.SkinFiltered;
            return ImageBlocker.NoSelectedSubmission;
        }
        if (current.Frame < observed + 2 || current.Frame < selectedFrame + 2) return ImageBlocker.FrameSettling;
        return ImageBlocker.None;
    }

    private static global::XASlave.Localization.UiMessage DescribeBlocker(ImageBlocker blocker) => blocker switch
    {
        ImageBlocker.NoFilter => global::XASlave.Localization.UiMessage.Key("S66E9A08B573C19E4"),
        ImageBlocker.Expired => global::XASlave.Localization.UiMessage.Key("SF9CF73BB484F4724"),
        ImageBlocker.Invalidated => global::XASlave.Localization.UiMessage.Key("S7A503CF883DEFA50"),
        ImageBlocker.MissingModels => global::XASlave.Localization.UiMessage.Key("S8C0770C056A42157"),
        ImageBlocker.NoSceneSubmission => global::XASlave.Localization.UiMessage.Key("S99869E9A4132161D"),
        ImageBlocker.NoSelectedSubmission => global::XASlave.Localization.UiMessage.Key("SB5DD881BC65AF571"),
        ImageBlocker.SkinFiltered => global::XASlave.Localization.UiMessage.Key("S2C484313D4D042F3"),
        ImageBlocker.FrameSettling => global::XASlave.Localization.UiMessage.Key("S0106FDC9C4372ABF"),
        _ => global::XASlave.Localization.UiMessage.Key("S1AC772397839B282"),
    };

    private string DescribeSelection()
    {
        var choice = choices.FirstOrDefault(value => value.Key == selection);
        return $"key={selection}; item={choice?.ItemId}; label={choice?.Label}";
    }

    private static string DescribeCounters(Filter current)
    {
        var counters = current.Diagnostics;
        var rejections = new List<string>();
        for (var index = 0; index < counters.Rejected.Length; index++)
        {
            var count = Interlocked.Read(ref counters.Rejected[index]);
            if (count != 0) rejections.Add($"{(RenderRejection)index}={count}");
        }
        var error = Volatile.Read(ref counters.Error);
        var masks = string.Join(",", current.Models.Where(model => model.Choice == current.Selected).Take(8)
            .Select(model => $"slot{model.Slot}:attributes=0x{model.Attributes:X}/shapes=0x{model.Shapes:X}"));
        return $"revision={current.Revision}; frame={current.Frame}; observedFrame={Interlocked.Read(ref counters.ObservedFrame)}; "
            + $"selectedFrame={Interlocked.Read(ref counters.SelectedFrame)}; owned={Interlocked.Read(ref counters.Owned)}; "
            + $"matched={Interlocked.Read(ref counters.Matched)}; selected={Interlocked.Read(ref counters.Selected)}; "
            + $"forwarded={Interlocked.Read(ref counters.Forwarded)}; rejected=[{string.Join(",", rejections)}]; "
            + $"masks=[{masks}]; error={error?.GetType().Name}: {error?.Message}";
    }

    private void LogFilterChange(Filter? previous, Filter current, string reason)
    {
        NearbyZoneNativeBinding.RequireFramework();
        var now = Environment.TickCount64;
        if (lastFilterDiagnosticAt != 0 && now - lastFilterDiagnosticAt < 2000) return;
        lastFilterDiagnosticAt = now;
        var selectedModels = current.Models.Where(model => model.Choice == current.Selected).ToArray();
        // These are bounded, captured values. Logging never dereferences render-worker pointers.
        var mapping = string.Join(",", current.Models.GroupBy(model => model.Choice)
            .Select(group => $"{group.Key}={group.Count()}"));
        var details = string.Join(" | ", selectedModels.Take(8).Select(model =>
            $"slot={model.Slot}; model=0x{model.Model:X}; resource=0x{model.Resource:X}; owner=0x{model.Owner:X}; "
            + $"materials={model.Materials.Length}; skin={model.Materials.Count(material => material.Skin)}; "
            + $"attributes=0x{model.Attributes:X}; shapes=0x{model.Shapes:X}; "
            + $"shaders=[{string.Join(",", model.Materials.Select(material => material.Shader).Distinct().Take(16))}]"));
        Plugin.Log.Information("[View Item] Filter capture: {Selection}; reason={Reason}; revision={Revision}; "
            + "scene={Scene}; owners={Owners}; models={Models}; selectedModels={SelectedModels}; choices=[{Mapping}]; "
            + "selected=[{Details}]; previous=[{Previous}].", DescribeSelection(), reason, current.Revision,
            scene.ReadinessDiagnostic, current.Owners.Length, current.Models.Length, selectedModels.Length, mapping,
            details, previous == null ? "none" : DescribeCounters(previous));
    }

    private void DrainRenderDiagnostics()
    {
        NearbyZoneNativeBinding.RequireFramework();
        DrainPendingRenderFailure();
        if (selection == null) return;
        var current = Volatile.Read(ref filter);
        var blocker = GetImageBlocker(current);
        var state = $"{blocker}; {StatusText}; {scene.ReadinessDiagnostic}; {scene.CameraDiagnostic}";
        var now = Environment.TickCount64;
        // Report changes and recovery once, then one summary every ten seconds
        // while blocked. A changing revision/counter cannot defeat the rate cap.
        if (lastDiagnosticAt != 0 && (now - lastDiagnosticAt < 2000
            || (state == lastDiagnostic && (blocker == ImageBlocker.None || now - lastDiagnosticAt < 10000)))) return;
        lastDiagnostic = state; lastDiagnosticAt = now;
        Plugin.Log.Information("[View Item] Preview state: {Selection}; blocker={Blocker}; status={Status}; "
            + "scene={Scene}; camera={Camera}; texture={Texture}; ageMs={Age}; render=[{Render}].",
            DescribeSelection(), blocker, StatusText, scene.ReadinessDiagnostic, scene.CameraDiagnostic, textureDiagnostic,
            now - revisionStarted, current == null ? "no filter" : DescribeCounters(current));
    }

    private void DrainPendingRenderFailure(bool force = false)
    {
        NearbyZoneNativeBinding.RequireFramework();
        var now = Environment.TickCount64;
        if (!force && lastRenderErrorAt != 0 && now - lastRenderErrorAt < 2000) return;
        var failed = Interlocked.Exchange(ref pendingRenderFailure, null);
        var textureChange = pendingTextureDiagnostic;
        pendingTextureDiagnostic = null;
        if (failed == null && textureChange == null) return;
        lastRenderErrorAt = now;
        if (textureChange != null)
            Plugin.Log.Information("[View Item] Texture confirmation: {Diagnostic}.", textureChange);
        if (failed == null) return;
        var error = Volatile.Read(ref failed.Diagnostics.Error);
        if (error != null)
            Plugin.Log.Warning(error, "[View Item] Render failure: {Selection}; render=[{Render}].",
                failed.SelectionInfo, DescribeCounters(failed));
        else
            Plugin.Log.Information("[View Item] Deferred render rejection: {Selection}; render=[{Render}].",
                failed.SelectionInfo, DescribeCounters(failed));
    }

    private static string DescribeTextureProblem(nint srv, long width, long height, long allocatedWidth, long allocatedHeight)
    {
        if (srv == 0) return "missing-SRV";
        if (width < 1 || height < 1) return "empty-actual-dimensions";
        if (width > 16384 || height > 16384) return "oversized-actual-dimensions";
        if (allocatedWidth < width || allocatedHeight < height) return "allocation-smaller-than-image";
        if (allocatedWidth > 16384 || allocatedHeight > 16384) return "oversized-allocation";
        return string.Empty;
    }

    internal bool TryGetImage(out IDalamudTextureWrap image, out Vector2 size)
    {
        lock (scene.SyncRoot)
        {
            image = null!; size = default;
            if (disposed || source == null) return false;
            Validate();
            var unavailable = choices.FirstOrDefault(value => value.Key == selection)?.UnavailableReason;
            if (!string.IsNullOrEmpty(unavailable)) { StatusText = global::XASlave.Localization.UiMessage.Store(ref statusMessage, choices.First(value => value.Key == selection).UnavailablePresentation); return false; }
            if (scene.GetCharacter() == null) { StatusText = global::XASlave.Localization.UiMessage.Store(ref statusMessage, scene.StatusTextMessage); return false; }
            var current = Volatile.Read(ref filter);
            var blocker = GetImageBlocker(current);
            if (blocker != ImageBlocker.None || current == null)
            {
                StatusText = global::XASlave.Localization.UiMessage.Store(ref statusMessage, DescribeBlocker(blocker));
                return false;
            }
            var manager = RenderTargetManager.Instance();
            NearbyZoneNativeBinding.RequireReadable((nint)manager, sizeof(RenderTargetManager));
            var texture = manager->GetCharaViewTexture(scene.SlotIndex);
            NearbyZoneNativeBinding.RequireReadable((nint)texture, sizeof(Texture));
            textureDiagnostic = $"texture=0x{(nint)texture:X}; srv=0x{(nint)texture->D3D11ShaderResourceView:X}; "
                + $"actual={texture->ActualWidth}x{texture->ActualHeight}; allocated={texture->AllocatedWidth}x{texture->AllocatedHeight}";
            var textureProblem = DescribeTextureProblem((nint)texture->D3D11ShaderResourceView,
                texture->ActualWidth, texture->ActualHeight, texture->AllocatedWidth, texture->AllocatedHeight);
            if (textureProblem.Length != 0)
            {
                textureDiagnostic += "; rejected=" + textureProblem;
                RememberTextureDiagnostic();
                StatusText = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Key("S9018C0FA7D78E95F")); return false;
            }
            var textureId = (nint)texture->D3D11ShaderResourceView;
            size = new Vector2(texture->ActualWidth, texture->ActualHeight);
            var freshImage = new ImageIdentity((nint)texture, textureId, size,
                new Vector2(texture->AllocatedWidth, texture->AllocatedHeight));
            if (imageIdentity != freshImage)
            {
                ReleaseImage();
                imageIdentity = freshImage;
                textureDiagnostic += "; state=identity-changed-reconfirming";
                RememberTextureDiagnostic();
                InvalidateFilter(current);
                revisionStarted = Environment.TickCount64;
                size = default;
                StatusText = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Key("S82F5549A41369806"));
                return false;
            }
            if (!IsCurrentFilter(current))
            { size = default; return false; }
            image = RetainImage(textureId, size);
            textureDiagnostic += "; state=retained";
            ImageUvMax = new Vector2((float)texture->ActualWidth / texture->AllocatedWidth,
                (float)texture->ActualHeight / texture->AllocatedHeight);
            StatusText = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Key("S1AC772397839B282"));
            return true;
        }
    }

    private IDalamudTextureWrap RetainImage(nint textureId, Vector2 size)
    {
        if (retainedImage == null)
        {
            IDalamudTextureWrap borrowed = new BorrowedTexture(new ImTextureID(textureId),
                checked((int)size.X), checked((int)size.Y));
            retainedImage = borrowed.CreateWrapSharingLowLevelResource();
        }
        return retainedImage;
    }

    private void RememberTextureDiagnostic()
    {
        if (pendingTextureDiagnostic == null)
            pendingTextureDiagnostic = DescribeSelection() + "; " + textureDiagnostic;
    }

    private void ReleaseImage()
    {
        var image = retainedImage;
        retainedImage = null;
        // Dalamud releases the retained SRV after draw submission completes.
        image?.Dispose();
    }

    private sealed class BorrowedTexture(ImTextureID handle, int width, int height) : IDalamudTextureWrap
    {
        public ImTextureID Handle => handle;
        public int Width => width;
        public int Height => height;
        public void Dispose() { }
    }

    internal void Camera(float yaw, float pitch, float zoom, float panX, float panY, float roll = 0)
    {
        Validate();
        if (source == null || !float.IsFinite(yaw) || !float.IsFinite(pitch) || !float.IsFinite(zoom)
            || !float.IsFinite(panX) || !float.IsFinite(panY) || !float.IsFinite(roll)
            || (yaw == 0 && pitch == 0 && zoom == 0 && panX == 0 && panY == 0 && roll == 0)) return;
        lock (scene.SyncRoot)
        {
            ResetRecordedAnimation();
            scene.Camera(yaw, pitch, zoom, panX, panY, roll);
        }
    }

    internal void RequestViewport(Vector2 size)
    {
        if (!float.IsFinite(size.X) || !float.IsFinite(size.Y) || size.X < 1 || size.Y < 1) return;
        size = Vector2.Clamp(size, new Vector2(1), new Vector2(16384));
        if (requestedViewport != size)
        {
            requestedViewport = size;
            viewportChangedAt = Environment.TickCount64;
        }
        if (viewport == size) return;
        // Coalesce resize events before clearing recorded frames or touching the
        // native camera. The first usable canvas does not need to wait.
        if (viewport != Vector2.Zero && Environment.TickCount64 - viewportChangedAt < 150) return;
        SetViewport(size);
    }

    internal void SetViewport(Vector2 size)
    {
        Validate();
        if (!float.IsFinite(size.X) || !float.IsFinite(size.Y) || size.X < 1 || size.Y < 1) return;
        size = Vector2.Clamp(size, new Vector2(1), new Vector2(16384));
        if (viewport == size) return;
        lock (scene.SyncRoot)
        {
            ResetRecordedAnimation();
            scene.SetViewport(size); viewport = size;
        }
    }

    internal void ResetCamera()
    {
        Validate();
        if (source == null) return;
        lock (scene.SyncRoot)
        {
            ResetRecordedAnimation();
            scene.ResetCamera();
        }
    }

    private void RetireSceneImage()
    {
        // Native preemption can arrive on another thread. Only retire immutable
        // render admission here; the framework owns wrapper and revision state.
        Volatile.Write(ref filter, null);
        Interlocked.Exchange(ref sceneImageRetirement, 1);
    }

    private void DrainSceneImageRetirement()
    {
        NearbyZoneNativeBinding.RequireFramework();
        if (Interlocked.Exchange(ref sceneImageRetirement, 0) == 0) return;
        animationClip.Reset();
        ReleaseImage(); imageIdentity = null;
        textureDiagnostic = "scene-retired";
        InvalidateRenderConfirmation();
    }

    private void InvalidateRenderConfirmation()
    {
        Volatile.Write(ref filter, null);
        revision++;
        revisionStarted = Environment.TickCount64;
    }

    internal void Clear()
    {
        NearbyZoneNativeBinding.RequireFramework();
        Furniture.Clear();
        DrainPendingRenderFailure(true);
        RetireSceneImage();
        DrainSceneImageRetirement();
        scene.Clear();
        DrainSceneImageRetirement();
        source = null; ornament = null; minion = mount = null; catalog = null;
        selection = null; choices = Array.Empty<ViewSingleItemChoice>();
        revision++;
        lastDiagnostic = string.Empty; lastDiagnosticAt = lastFilterDiagnosticAt = 0;
        lastRenderErrorAt = 0; textureDiagnostic = "not-read";
        ImageUvMax = Vector2.One;
        imageIdentity = null;
        UnavailableReason = ReadinessDiagnostic = string.Empty;
    }

    public void Dispose()
    {
        if (disposed) return;
        Clear(); disposed = true;
        Furniture.Dispose();
        animationClip.Dispose();
        scene.Dispose();
        meshHook.Disable(); meshHook.Dispose();
    }

    private static int ModelChoice(int slot) => slot switch
    {
        0 => 2, 1 => 3, 2 => 4, 3 => 6, 4 => 7, 5 => 8, 6 => 9, 7 => 10,
        8 => 11, 9 => 12, 10 => 100, 16 => 101, _ => -1,
    };

    private static global::XASlave.Localization.UiMessage SlotLabel(int slot) => slot switch
    {
        0 => global::XASlave.Localization.UiMessage.Key("S1710782C5D15B913"), 1 => global::XASlave.Localization.UiMessage.Key("SB68A5DF103BDB195"), 2 => global::XASlave.Localization.UiMessage.Key("S7E94675FF2DEA116"), 3 => global::XASlave.Localization.UiMessage.Key("SA592855D0508D161"), 4 => global::XASlave.Localization.UiMessage.Key("S6B7204778686145F"), 6 => global::XASlave.Localization.UiMessage.Key("SE4633585E7BBA115"),
        7 => global::XASlave.Localization.UiMessage.Key("SB5EFA9EC3F5F8D45"), 8 => global::XASlave.Localization.UiMessage.Key("SA532E980AE2F48EB"), 9 => global::XASlave.Localization.UiMessage.Key("S6A88445BBF65F756"), 10 => global::XASlave.Localization.UiMessage.Key("SA9A8BED2FC5B6067"), 11 => global::XASlave.Localization.UiMessage.Key("S44D9F0B94BCAB574"), 12 => global::XASlave.Localization.UiMessage.Key("S2B4543478B84CB8E"), _ => global::XASlave.Localization.UiMessage.Key("S652BCC3A47842889"),
    };
}
