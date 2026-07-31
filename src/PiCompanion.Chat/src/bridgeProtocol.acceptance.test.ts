import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
import { bridgeProtocolVersion } from './types/bridge'

describe('desktop bridge protocol contract', () => {
  it('keeps the Web app and desktop host on the same protocol version', () => {
    const desktopContracts = readFileSync(resolve(
      process.cwd(),
      '../PiCompanion.Desktop/ChatHost/BridgeContracts.cs',
    ), 'utf8')
    const desktopVersion = Number(desktopContracts.match(/ProtocolVersion\s*=\s*(\d+)/)?.[1])

    expect(desktopVersion).toBe(bridgeProtocolVersion)
  })

  it('persists conversation detail and exposes mutually exclusive native shortcuts', () => {
    const appSettings = readFileSync(resolve(
      process.cwd(),
      '../PiCompanion.Application/Settings/AppSettings.cs',
    ), 'utf8')
    const mainWindowXaml = readFileSync(resolve(
      process.cwd(),
      '../PiCompanion.Desktop/MainWindow.xaml',
    ), 'utf8')
    const mainWindow = readFileSync(resolve(
      process.cwd(),
      '../PiCompanion.Desktop/MainWindow.xaml.cs',
    ), 'utf8')
    const desktopLocalizer = readFileSync(resolve(
      process.cwd(),
      '../PiCompanion.Desktop/Localization/DesktopLocalizer.cs',
    ), 'utf8')

    expect(appSettings).toContain('string? ConversationDetailLevel = "normal"')
    expect(appSettings).toContain('["summary", "normal", "verbose"]')
    expect(mainWindowXaml).toContain('Tag="summary" IsCheckable="True" Click="OnConversationDetailClick"')
    expect(mainWindowXaml).toContain('Tag="normal" IsCheckable="True" Click="OnConversationDetailClick"')
    expect(mainWindowXaml).toContain('Tag="verbose" IsCheckable="True" Click="OnConversationDetailClick"')
    expect(mainWindowXaml).toContain('<MenuItem Header="对话显示">')
    expect(mainWindowXaml).toContain('Data="M 4 0 L 0 4 L 4 8"')
    expect(mainWindowXaml).toContain('Placement="Left"')
    expect(mainWindowXaml).toMatch(/<Path x:Name="SubmenuArrowLeft"[\s\S]*?Grid.Column="0"/u)
    expect(mainWindowXaml).toMatch(/<Path x:Name="SubmenuArrowRight"[\s\S]*?Grid.Column="2"/u)
    expect(mainWindowXaml).toMatch(/<Path x:Name="SelectionCheck"[\s\S]*?Grid.Column="0"/u)
    expect(mainWindowXaml).toContain('<TextBlock Grid.Column="1"')
    expect(mainWindowXaml.match(/<Separator Margin="4,3" \/>/gu)).toHaveLength(1)
    expect(mainWindowXaml).toContain('Foreground="{TemplateBinding Foreground}"')
    expect(desktopLocalizer).toContain('["对话显示"] = "Conversation display"')
    expect(desktopLocalizer).toContain('["摘要"] = "Summary"')
    expect(mainWindow).toContain('General = current.General with { ConversationDetailLevel = detailLevel }')
    expect(mainWindow).toContain('PostSettingsSnapshot();')
    expect(mainWindow).toContain('item.IsChecked = string.Equals(selected, detailLevel, StringComparison.Ordinal);')
    expect(mainWindow).toContain('item.Header = label;')
    expect(mainWindow).not.toContain('OnChatMoreMenuOpened')
    expect(mainWindow).not.toContain('OnConversationDetailSubmenuOpened')
  })

  it('keeps model visibility in Companion while retaining the Pi scope only for migration', () => {
    const appSettings = readFileSync(resolve(
      process.cwd(),
      '../PiCompanion.Application/Settings/AppSettings.cs',
    ), 'utf8')
    const mainWindow = readFileSync(resolve(
      process.cwd(),
      '../PiCompanion.Desktop/MainWindow.xaml.cs',
    ), 'utf8')
    const promptComposer = readFileSync(resolve(
      process.cwd(),
      '../PiCompanion.Desktop/PromptComposer/PromptComposerWindow.xaml.cs',
    ), 'utf8')
    const piSettings = readFileSync(resolve(
      process.cwd(),
      '../PiCompanion.Extension/pi-settings.mjs',
    ), 'utf8')

    expect(appSettings).toContain('IReadOnlyList<string> HiddenModelReferences')
    expect(appSettings).toContain('TryMigrateLegacyModelVisibility')
    expect(promptComposer).toContain('_settings.Current.ModelVisibility!.HiddenModelReferences')
    expect(mainWindow).not.toContain('case "SavePiEnabledModels":')
    expect(piSettings).not.toContain("input.action === 'save-enabled-models'")
  })

  it('contracts grouped native discovery, guarded removal, and direct local import', () => {
    const desktopContracts = readFileSync(resolve(
      process.cwd(),
      '../PiCompanion.Desktop/ChatHost/BridgeContracts.cs',
    ), 'utf8')
    const mainWindow = readFileSync(resolve(
      process.cwd(),
      '../PiCompanion.Desktop/MainWindow.xaml.cs',
    ), 'utf8')
    const rpcBackend = readFileSync(resolve(
      process.cwd(),
      '../PiCompanion.Application/PiRpc/PiRpcBackend.cs',
    ), 'utf8')
    const metadataGenerator = readFileSync(resolve(
      process.cwd(),
      '../PiCompanion.Application/Tasks/PiTaskMetadataGenerator.cs',
    ), 'utf8')

    expect(bridgeProtocolVersion).toBe(60)
    expect(mainWindow).toContain('case "SetWorkspaceTrustDecision":')
    expect(mainWindow).toContain('case "LoadSkills":')
    expect(mainWindow).toContain('case "TrustSkillWorkspace":')
    expect(mainWindow).toContain('case "RemoveSkillInstallation":')
    expect(mainWindow).toContain('case "BeginSkillImport":')
    expect(mainWindow).toContain('case "PrepareSkillImport":')
    expect(mainWindow).toContain('case "ConfirmSkillImport":')
    expect(mainWindow).toContain('case "CancelSkillImport":')
    expect([...mainWindow.matchAll(/case "([^"]*Skill[^"]*)":/gu)]
      .map(match => match[1])).toEqual([
        'LoadSkills',
        'TrustSkillWorkspace',
        'RemoveSkillInstallation',
        'BeginSkillImport',
        'PrepareSkillImport',
        'ConfirmSkillImport',
        'CancelSkillImport',
      ])
    expect(mainWindow).toContain('"SkillsLoaded",')
    expect(mainWindow).toContain('"SkillRemovalCompleted",')
    expect(mainWindow).toContain('"SkillWorkspaceTrustCompleted",')
    expect(mainWindow).toContain('"SkillImportSourceInspected",')
    expect(mainWindow).toContain('"SkillImportReady",')
    expect(mainWindow).toContain('"SkillImportCompleted",')
    expect(mainWindow).toContain('PostMessage(')
    expect(mainWindow).toContain('new SkillDiscoveryWorkspace(')
    expect(desktopContracts).toContain('internal sealed record LoadSkillsRequestDto(')
    expect(desktopContracts).toContain('internal sealed record TrustSkillWorkspaceRequestDto(')
    expect(desktopContracts).toContain('internal sealed record SetWorkspaceTrustDecisionRequestDto(')
    expect(desktopContracts).toContain('internal sealed record SkillsLoadedDto(')
    expect(desktopContracts).toContain('internal sealed record DiscoveredSkillDto(')
    expect(desktopContracts).toContain('internal sealed record SkillContentVariantDto(')
    expect(desktopContracts).toContain('internal sealed record SkillInstallationDto(')
    expect(desktopContracts).toContain('internal sealed record SkillOriginDto(')
    expect(desktopContracts).toContain('bool IsCompatibilityLink,')
    expect(desktopContracts).toContain('string? LinkTarget);')
    expect([...desktopContracts.matchAll(/"(skill-[^"]+)"/gu)]
      .map(match => match[1])).toEqual([
        'skill-native-discovery',
        'skill-content-fingerprints',
        'skill-pi-removal',
        'skill-local-direct-import',
        'skill-workspace-trust',
      ])
    expect(rpcBackend).not.toContain('"--no-skills"')
    expect(rpcBackend).not.toContain('"--skill"')
    expect(metadataGenerator).not.toContain('"--no-skills"')
  })

  it('drops stale task execution-default updates during task handoff', () => {
    const mainWindow = readFileSync(resolve(
      process.cwd(),
      '../PiCompanion.Desktop/MainWindow.xaml.cs',
    ), 'utf8')
    const app = readFileSync(resolve(process.cwd(), 'src/App.vue'), 'utf8')

    expect(mainWindow).toContain('if (_coordinator.Current?.TaskId == taskId)')
    expect(app).toContain('current.id !== taskId')
    expect(app).toContain('if (executionDefaultsTimer) window.clearTimeout(executionDefaultsTimer)')
    expect(app).toContain('store.bridgeError = null')
  })

  it('opens a Monitor-created task in the chat view', () => {
    const mainWindow = readFileSync(resolve(
      process.cwd(),
      '../PiCompanion.Desktop/MainWindow.xaml.cs',
    ), 'utf8')
    const openNewTask = mainWindow.match(
      /public void OpenNewTask\(\)[\s\S]*?\n    \}\n\n    private async void OnLoaded/u,
    )?.[0]

    expect(openNewTask).toContain('BeginNewTask();')
    expect(openNewTask).toContain('_openCurrentTaskWhenReady = true;')
    expect(openNewTask).toContain('PostOpenCurrentTask();')
  })

  it('owns clipboard images from draft removal through task asset promotion', () => {
    const mainWindow = readFileSync(resolve(
      process.cwd(),
      '../PiCompanion.Desktop/MainWindow.xaml.cs',
    ), 'utf8')
    const attachmentStaging = readFileSync(resolve(
      process.cwd(),
      '../PiCompanion.Application/Tasks/AttachmentStagingService.cs',
    ), 'utf8')
    const composerDraft = readFileSync(resolve(
      process.cwd(),
      '../PiCompanion.Desktop/Shell/ComposerDraft.cs',
    ), 'utf8')

    expect(mainWindow).toContain('case "AddClipboardImageAttachment":')
    expect(mainWindow).toContain('Convert.FromBase64String(ReadString(payload, "data"))')
    expect(mainWindow).toContain('Path.Combine(GetDataDirectory(), "clipboard-attachments")')
    expect(mainWindow).toContain('AddAttachments(payload, [path]);')
    expect(mainWindow).toContain('_clipboardDraftAttachments.Add(path);')
    expect(mainWindow).toContain('DeleteClipboardDraftAttachment(path);')
    expect(mainWindow).toContain('ReadOptionalString(payload, "prompt") ?? string.Empty')
    expect(mainWindow).toContain('string.IsNullOrWhiteSpace(prompt) && composerAttachments.Count == 0')
    expect(mainWindow).toContain('private const int MaximumClipboardImageBytes = 10 * 1024 * 1024;')
    expect(attachmentStaging).toContain('IReadOnlyList<string> PersistentPaths')
    expect(attachmentStaging).toContain('Path.Combine(taskRoot, "assets")')
    expect(attachmentStaging).toContain('persistentPaths.Add(assetDestination);')
    expect(attachmentStaging).toContain('DeleteFile(source);')
    expect(composerDraft).toContain('string? PreviewDataUrl = null')
    expect(composerDraft).toContain('data:image/png;base64,')
  })

  it('validates workspace location actions in the desktop host', () => {
    const mainWindow = readFileSync(resolve(
      process.cwd(),
      '../PiCompanion.Desktop/MainWindow.xaml.cs',
    ), 'utf8')

    expect(mainWindow).toContain('case "OpenWorkspaceLocation":')
    expect(mainWindow).toContain('current?.HasUserWorkspace != true')
    expect(mainWindow).toContain('var workingDirectory = RequireCurrentWorkspace(payload);')
    expect(mainWindow).toContain('ProcessStartInfo("wt.exe")')
    expect(mainWindow).toContain('System.Windows.Clipboard.SetText(workingDirectory)')
  })

  it('creates independent workspaces and resolves workspace shortcuts by id', () => {
    const mainWindow = readFileSync(resolve(
      process.cwd(),
      '../PiCompanion.Desktop/MainWindow.xaml.cs',
    ), 'utf8')

    expect(mainWindow).toContain('case "CreateWorkspace":')
    expect(mainWindow).toContain('case "HideWorkspace":')
    expect(mainWindow).toContain('case "NewTaskInWorkspace":')
    expect(mainWindow).toContain('_coordinator.CreateWorkspace(dialog.FolderName)')
    expect(mainWindow).toContain('candidate.Id == workspaceId')
  })

  it('switches the focused task from an actual-position Monitor wheel picker', () => {
    const monitorXaml = readFileSync(resolve(
      process.cwd(),
      '../PiCompanion.Desktop/Monitor/MonitorWindow.xaml',
    ), 'utf8')
    const monitorCode = readFileSync(resolve(
      process.cwd(),
      '../PiCompanion.Desktop/Monitor/MonitorWindow.xaml.cs',
    ), 'utf8')

    expect(monitorXaml).toContain('x:Name="HeaderTitleButton"')
    expect(monitorXaml.match(/x:Name="HeaderTitleButton"/gu)).toHaveLength(1)
    expect(monitorXaml).toContain('PreviewMouseLeftButtonDown="OnTaskSelectorMouseDown"')
    expect(monitorXaml).toContain('x:Name="TaskPickerPopup"')
    expect(monitorXaml).toContain('x:Name="TaskPickerList"')
    expect(monitorXaml).toContain('<Border Width="310"')
    expect(monitorXaml).toContain('x:Name="HeaderTaskScrollCue"')
    expect(monitorXaml).toContain('ContextMenuService.ShowOnDisabled="True"')
    expect(monitorXaml).toContain('StaysOpen="True"')
    expect(monitorXaml).toContain('PreviewMouseWheel="OnTaskPickerMouseWheel"')
    expect(monitorXaml).toContain('PreviewMouseLeftButtonDown="OnTaskPickerItemClick"')
    expect(monitorXaml).toContain('PreviewKeyDown="OnTaskPickerKeyDown"')
    expect(monitorXaml).toContain('SelectionChanged="OnTaskPickerSelectionChanged"')
    expect(monitorXaml).toContain('x:Name="TaskStatusDot"')
    expect(monitorXaml).not.toContain('TargetName="TaskStatusDot" Property="Visibility"')
    expect(monitorXaml).not.toContain('MonitorTaskSelectorContextMenu')
    expect(monitorCode).toContain('private const int RecentTaskChoiceLimit = 5;')
    expect(monitorCode).toContain('_coordinator.RecentTasks')
    expect(monitorCode).toContain('.Take(RecentTaskChoiceLimit)')
    expect(monitorCode).not.toContain('_coordinator.ActiveTasks')
    expect(monitorCode).toContain('_coordinator.SelectTask(taskId);')
    expect(monitorCode).toContain('DispatcherPriority.Input')
    expect(monitorCode).toContain('TaskPickerPopup.IsOpen = true')
    expect(monitorCode).toContain('var hasAlternativeTask = choices.Any(choice => !choice.IsCurrent);')
    expect(monitorCode).toContain('if (!choices.Any(choice => !choice.IsCurrent))')
    expect(monitorCode).not.toContain('OnViewAllTasksClick')
    expect(monitorCode).not.toContain('new Separator')
    expect(monitorCode).not.toContain('WpfMenuItem')
    expect(monitorCode).toContain('TaskPickerPopup.CustomPopupPlacementCallback = PlaceTaskPicker')
    expect(monitorCode).toContain('selectedIndex * TaskPickerRowHeight')
    expect(monitorCode).toContain('(targetSize.Width - popupSize.Width) / 2')
    expect(monitorCode).toContain('AlignTaskPickerWithoutScreenClamping()')
    expect(monitorCode).toContain('target.PointToScreen(')
    expect(monitorCode).toContain('SetWindowPos(')
    expect(monitorCode).toContain('TaskPickerWheelThreshold = 120')
    expect(monitorCode).toContain('TaskPickerWheelThrottleMilliseconds = 90')
    expect(monitorCode).toContain('MoveTaskPickerSelection(direction)')
    expect(monitorCode).toContain('TaskPickerAutoCloseDelayMilliseconds = 500')
    expect(monitorCode).toContain('var openedForWheel = !TaskPickerPopup.IsOpen')
    expect(monitorCode).toContain('OpenTaskPicker(button, focusList: false)')
    expect(monitorCode).toContain('_taskPickerAutoCloseTimer.Start()')
    expect(monitorCode).toContain('TaskPickerPopup.IsOpen = false;')
  })

  it('uses the non-interactive Monitor surface as the drag region', () => {
    const monitorXaml = readFileSync(resolve(
      process.cwd(),
      '../PiCompanion.Desktop/Monitor/MonitorWindow.xaml',
    ), 'utf8')
    const monitorCode = readFileSync(resolve(
      process.cwd(),
      '../PiCompanion.Desktop/Monitor/MonitorWindow.xaml.cs',
    ), 'utf8')

    expect(monitorXaml).toContain('PreviewMouseDown="OnDragSurfaceMouseLeftButtonDown"')
    expect(monitorXaml).not.toContain('PreviewMouseUp="OnDragSurfaceMouseLeftButtonUp"')
    expect(monitorXaml).not.toContain('PreviewMouseMove="OnDragSurfaceMouseMove"')
    expect(monitorCode).toContain('DragMove();')
    expect(monitorCode).not.toContain('RootBorder.CaptureMouse()')
    expect(monitorCode).toContain('HeaderTitleButton.IsMouseOver')
    expect(monitorCode).not.toContain('handledEventsToo: true')
    expect(monitorCode).toContain('IsDragInteractionSource(e.OriginalSource as DependencyObject)')
    expect(monitorCode).toContain('FindVisualAncestor<WpfButtonBase>(source)')
    expect(monitorCode).toContain('FindVisualAncestor<WpfTextBoxBase>(source)')
    expect(monitorCode).toMatch(
      /private void OnMouseDoubleClick[\s\S]*?IsDragInteractionSource\(e\.OriginalSource as DependencyObject\)/u,
    )
  })
})
