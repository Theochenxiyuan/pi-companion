import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

const source = (relativePath: string) => readFileSync(
  fileURLToPath(new URL(relativePath, import.meta.url)),
  'utf8',
)

const monitorCode = source('../../PiCompanion.Desktop/Monitor/MonitorWindow.xaml.cs')
const monitorAnimationCode = source('../../PiCompanion.Desktop/Monitor/MonitorWindow.StatusAnimation.cs')
const monitorView = source('../../PiCompanion.Desktop/Monitor/MonitorWindow.xaml')
const promptComposerCode = source('../../PiCompanion.Desktop/PromptComposer/PromptComposerWindow.xaml.cs')
const promptComposerView = source('../../PiCompanion.Desktop/PromptComposer/PromptComposerWindow.xaml')
const skillCompletionCode = source('../../PiCompanion.Desktop/Skills/SkillCompletionController.cs')
const desktopLocalizer = source('../../PiCompanion.Desktop/Localization/DesktopLocalizer.cs')
const settingsModel = source('../../PiCompanion.Application/Settings/AppSettings.cs')
const settingsView = source('./components/SettingsModal.vue')
const bridgeTypes = source('./types/bridge.ts')

describe('Monitor expand and collapse behavior', () => {
  it('does not expand the Monitor from pointer hover', () => {
    const mouseEnterHandler = monitorCode.match(
      /private void OnMonitorMouseEnter[\s\S]*?private void OnMonitorMouseLeave/u,
    )?.[0]

    expect(mouseEnterHandler).toContain('_autoCollapseTimer.Stop();')
    expect(mouseEnterHandler).not.toContain('Expand();')
    expect(monitorCode).not.toContain('_hoverExpandTimer')
    expect(settingsModel).not.toContain('HoverDelayMilliseconds')
    expect(settingsView).not.toContain('hoverDelayMilliseconds')
    expect(bridgeTypes).not.toContain('hoverDelayMilliseconds')
  })

  it('keeps configurable automatic collapse', () => {
    expect(monitorCode).toContain('_autoCollapseTimer.Start();')
    expect(monitorCode).toContain('_settings.AutoCollapseSeconds == 0')
    expect(settingsModel).toContain('int AutoCollapseSeconds')
    expect(settingsView).toContain('draft.monitor.autoCollapseSeconds')
  })

  it('uses accessible icon buttons for explicit expand and collapse', () => {
    expect(monitorView).not.toContain('Content="展开"')
    expect(monitorView).not.toContain('Content="收起"')
    expect(monitorView).toContain('ToolTip="展开"')
    expect(monitorCode).toContain('DesktopLocalizer.Text("收起", "Collapse")')
    expect(monitorView).toContain('AutomationProperties.Name=')
    expect(monitorView).toContain('x:Name="HeaderExpandedToggleButton"')
    expect(monitorView).toContain('x:Name="HeaderExpandIcon"')
    expect(monitorView).toContain('x:Name="HeaderCollapseIcon"')
    expect(monitorView).toContain('Data="M 2,5 L 7.5,10.5 L 13,5"')
    expect(monitorView).toContain('Data="M 2,10 L 7.5,4.5 L 13,10"')
    expect(monitorView).toContain('x:Key="MonitorIconButton"')
    expect(monitorView).toContain('<ContentPresenter HorizontalAlignment="Center"')
  })

  it('uses a five-row wheel picker with the title and status as one target', () => {
    const pickerTarget = monitorView.match(
      /<Button x:Name="HeaderTitleButton"[\s\S]*?<\/Button>/u,
    )?.[0]
    const pickerItemTemplate = monitorView.match(
      /<ListBox x:Name="TaskPickerList"[\s\S]*?<ListBox\.ItemTemplate>[\s\S]*?<\/ListBox\.ItemTemplate>/u,
    )?.[0]
    const titleSelectorStyle = monitorView.match(
      /<Style x:Key="MonitorTitleSelectorButton"[\s\S]*?<Style x:Key="MonitorIconButton"/u,
    )?.[0]

    expect(pickerTarget).toContain('x:Name="HeaderTitle"')
    expect(pickerTarget).toContain('x:Name="HeaderStatus"')
    expect(pickerTarget).toContain('FontSize="{StaticResource TypographySizeBody}"')
    expect(pickerTarget).toContain('x:Name="HeaderTaskScrollCue"')
    expect(pickerTarget).toContain('PreviewMouseLeftButtonDown="OnTaskSelectorMouseDown"')
    expect(monitorView.match(/x:Name="HeaderTitleButton"/gu)).toHaveLength(1)
    expect(monitorView).toContain('<Setter Property="Height" Value="54" />')
    expect(monitorView).toContain('Text="{Binding Status}"')
    expect(monitorView).toContain('Text="{Binding Workspace}"')
    expect(monitorCode).toContain('.Take(RecentTaskChoiceLimit)')
    expect(monitorCode).toContain('Math.Clamp(')
    expect(monitorCode).toContain('selectedRowTop = TaskPickerPadding + (selectedIndex * TaskPickerRowHeight)')
    expect(monitorView).toContain('<Border Width="310"')
    expect(monitorView).toContain('Padding="4,6"')
    expect(monitorView).toContain('<Setter Property="Padding" Value="8,6" />')
    expect(pickerItemTemplate).toContain('FontSize="{StaticResource TypographySizeBody}"')
    expect(pickerItemTemplate).toContain('<Grid Margin="0,3,0,0">')
    expect(pickerItemTemplate).toContain('x:Name="TaskStatus"')
    expect(pickerItemTemplate).toContain('HorizontalAlignment="Right"')
    expect(pickerItemTemplate).toContain('VerticalAlignment="Center"')
    expect(monitorCode).toContain('(targetSize.Width - popupSize.Width) / 2')
    expect(monitorCode).toContain('AlignTaskPickerWithoutScreenClamping()')
    expect(monitorCode).toContain('target.PointToScreen(')
    expect(monitorCode).toContain('popupSource.Handle,')
    expect(monitorCode).toContain('SwpNoSize | SwpNoZOrder | SwpNoActivate')
    const unconstrainedPickerPlacement = monitorCode.match(
      /private void AlignTaskPickerWithoutScreenClamping\(\)[\s\S]*?private IReadOnlyList<MonitorTaskChoice>/u,
    )?.[0]
    expect(unconstrainedPickerPlacement).not.toContain('WorkArea')
    expect(unconstrainedPickerPlacement).not.toContain('Math.Clamp')
    expect(monitorCode).toContain('TaskPickerAutoCloseDelayMilliseconds = 500')
    expect(monitorCode).toContain('var direction = _taskPickerWheelDelta > 0 ? -1 : 1')
    expect(monitorCode).toContain('var openedForWheel = !TaskPickerPopup.IsOpen')
    expect(monitorCode).toContain('OpenTaskPicker(button, focusList: false)')
    expect(monitorCode).toContain('_taskPickerAutoCloseTimer.Start()')
    expect(monitorCode).toContain('ItemsControl.ContainerFromElement(TaskPickerList, source)')
    expect(monitorCode).toContain('_taskPickerWheelBlockedUntil')
    expect(monitorCode).toContain('TaskPickerPopup.IsOpen || HasActiveInputFocus()')
    const taskSelectorMouseDown = monitorCode.match(
      /private void OnTaskSelectorMouseDown[\s\S]*?private void OpenTaskPicker/u,
    )?.[0]
    expect(taskSelectorMouseDown).toContain('var moved = DragMonitorWindow();')
    expect(taskSelectorMouseDown?.indexOf('DragMonitorWindow()')).toBeLessThan(
      taskSelectorMouseDown?.indexOf('TaskPickerPopup.IsOpen') ?? -1,
    )
    expect(monitorCode).toContain('SystemParameters.MinimumHorizontalDragDistance')
    expect(monitorCode).toContain('SystemParameters.MinimumVerticalDragDistance')
    expect(monitorCode).toContain('WindowPlacementService.ConstrainToCursorWorkArea(this);')
    expect(monitorView).toContain('x:Key="MonitorTitleHoverBrush"')
    expect(monitorView).toContain('Opacity="0.5"')
    expect(monitorView).toContain('Value="{DynamicResource MonitorTitleHoverBrush}"')
    expect(titleSelectorStyle).not.toContain('<Trigger Property="IsPressed" Value="True">')
    expect(titleSelectorStyle).not.toContain('<Trigger Property="Tag" Value="TaskSelectorPressed">')
    expect(monitorView).toContain('PreviewMouseLeftButtonDown="OnTaskPickerItemClick"')
    expect(monitorView).not.toContain('Click="OnTaskSelectorClick"')
    expect(pickerItemTemplate).not.toContain('TargetName="TaskStatusDot" Property="Visibility"')
    expect(pickerItemTemplate).not.toContain('TargetName="TaskStatus" Property="Grid.Column"')
    expect(monitorCode).not.toContain('PlaceTaskSelectorMenu')
  })

  it('matches the collapsed shell to the expanded header and exposes the context menu across the card', () => {
    const rootBorder = monitorView.match(
      /<Border x:Name="RootBorder"[\s\S]*?<Border\.Effect>/u,
    )?.[0]
    const sharedHeader = monitorView.match(
      /<Grid x:Name="SharedHeader"[\s\S]*?<\/Grid>\s*<Grid x:Name="ExpandedBody"/u,
    )?.[0]

    expect(monitorView).toContain('Width="440"')
    expect(monitorView).toContain('Height="88"')
    expect(monitorCode).toContain('private const double CapsuleWidth = 440;')
    expect(monitorCode).toContain('private const double CapsuleHeight = 88;')
    expect(rootBorder).toContain('ContextMenu="{StaticResource MonitorContextMenu}"')
    expect(rootBorder).toContain('ContextMenuOpening="OnMonitorContextMenuOpening"')
    expect(rootBorder).toContain('ContextMenuService.ShowOnDisabled="True"')
    expect(monitorView).toContain('<Grid x:Name="MonitorLayout" Margin="16">')
    expect(monitorView).toContain('<Grid x:Name="SharedHeader">')
    expect(monitorView).toContain('<Grid x:Name="ExpandedBody"')
    expect(sharedHeader).toContain('FontSize="{StaticResource TypographySizeBody}"')
    expect(sharedHeader).toContain('Foreground="{DynamicResource TextSecondaryBrush}"')
    expect(sharedHeader).not.toContain('ContextMenu="{StaticResource MonitorContextMenu}"')
    expect(monitorCode).toContain('HeaderTitle.Text = "Pi Companion";')
    expect(monitorView.match(/x:Name="HeaderTitle"/gu)).toHaveLength(1)
    expect(monitorView).not.toContain('CapsulePanel')
    expect(monitorView).not.toContain('ExpandedPanel')
  })

  it('grows Monitor text inputs until a rounded scrolling limit', () => {
    const growingTextBoxStyle = monitorView.match(
      /<Style x:Key="MonitorGrowingTextBox"[\s\S]*?<\/Style>/u,
    )?.[0]

    expect(growingTextBoxStyle).toContain('<Setter Property="MinHeight" Value="36" />')
    expect(growingTextBoxStyle).toContain('<Setter Property="MaxHeight" Value="112" />')
    expect(growingTextBoxStyle).toContain('<Setter Property="AcceptsReturn" Value="True" />')
    expect(growingTextBoxStyle).toContain('<Setter Property="TextWrapping" Value="Wrap" />')
    expect(growingTextBoxStyle).toContain('<Setter Property="VerticalScrollBarVisibility" Value="Auto" />')
    expect(growingTextBoxStyle).toContain('CornerRadius="8"')
    expect(growingTextBoxStyle).not.toContain('Margin="{TemplateBinding Padding}"')
    expect(monitorView.split('Style="{StaticResource MonitorGrowingTextBox}"')).toHaveLength(4)
    expect(monitorView.split('PreviewKeyDown="OnMonitorInputPreviewKeyDown"')).toHaveLength(4)
    expect(monitorView).toContain('x:Name="DirectionButton" Content="发送新一轮" Focusable="False"')
    expect(monitorView.match(/Height="40" VerticalAlignment="Center"/gu)?.length).toBeGreaterThanOrEqual(3)
    expect(monitorCode).toContain('(Keyboard.Modifiers & ModifierKeys.Control) == 0')
    expect(monitorCode).toContain('await SubmitDirectionAsync();')
    expect(monitorCode).toContain('await SubmitAnswerAsync();')
    expect(monitorCode).toContain('await SubmitSelectedAnswerAsync();')
  })

  it('offers contextual skill completion only in task-composing inputs', () => {
    expect(promptComposerView).toContain('x:Name="SkillSuggestionPopup"')
    expect(promptComposerView).toContain('x:Name="SkillSuggestionList"')
    expect(promptComposerCode).toContain('PromptTextBox,')
    expect(promptComposerCode).toContain('TaskScopeKind.Workspace')
    expect(monitorView).toContain('PlacementTarget="{Binding ElementName=DirectionTextBox}"')
    expect(monitorCode).toContain('ReferenceEquals(sender, DirectionTextBox)')
    expect(monitorCode).toContain('_skillCompletion.HandlePreviewKeyDown(e)')
    expect(skillCompletionCode).toContain('SkillCompletionQuery.TryParse')
    expect(skillCompletionCode).toContain('SkillCompletionQuery.CreateInvocation(item.Name)')
    expect(skillCompletionCode).toContain('case Key.Down:')
    expect(skillCompletionCode).toContain('case Key.Up:')
    expect(skillCompletionCode).toContain('case Key.Tab:')
    expect(skillCompletionCode).toContain('case Key.Escape:')
    expect(monitorCode.match(/_skillCompletion\.HandlePreviewKeyDown\(e\)/gu)).toHaveLength(1)
  })

  it('animates the title status indicators without animating the result status dot', () => {
    expect(monitorView).toContain('x:Name="HeaderStatusHalo"')
    expect(monitorView).toContain('x:Name="HeaderStatusHaloScale"')
    expect(monitorView.match(/x:Name="HeaderStatusHalo"/gu)).toHaveLength(1)
    expect(monitorView).not.toContain('x:Name="ResultStatusBadge"')
    expect(monitorAnimationCode).toContain('HeaderStatusHalo')
    expect(monitorAnimationCode).not.toContain('CapsuleStatusHalo')
    expect(monitorAnimationCode).not.toContain('ExpandedStatusHalo')
    expect(monitorAnimationCode).not.toContain('ResultStatusDot')
  })

  it('uses warning yellow for approvals and running blue for questions', () => {
    expect(monitorCode).toContain('RunStatus.WaitingForApproval => "WarningBrush"')
    expect(monitorCode).toContain('RunStatus.WaitingForAnswer => "RunningBrush"')
    expect(monitorCode).toContain('waitingForAnswer ? "RunningSurfaceBrush" : "WarningTintBrush"')
    expect(monitorCode).toContain('waitingForAnswer ? "RunningBrush" : "WarningBrush"')
  })

  it('keeps long interaction details scrollable while pinning responsive actions', () => {
    expect(monitorView).toContain('<ScrollViewer Grid.Row="1" MaxHeight="180"')
    expect(monitorView).toContain('x:Name="InteractionPrompt"')
    expect(monitorView).toContain('x:Name="ApprovalActions" Grid.Row="2"')
    expect(monitorView).toContain('<WrapPanel x:Name="ApprovalActions"')
    expect(monitorView).toContain('x:Name="CopyInteractionButton"')
    expect(monitorCode).toContain('OnCopyInteractionClick')
    expect(monitorCode).toContain('Clipboard.SetText(InteractionPrompt.Text)')
  })

  it('connects the animation preference to every visible run-state animation', () => {
    expect(monitorAnimationCode).toContain('_settings.AnimationsEnabled')
    expect(monitorAnimationCode).toContain('SystemParameters.ClientAreaAnimation')
    expect(monitorAnimationCode).toContain('case RunStatus.Queued:')
    expect(monitorAnimationCode).toContain('case RunStatus.Starting:')
    expect(monitorAnimationCode).toContain('case RunStatus.Running:')
    expect(monitorAnimationCode).toContain('case RunStatus.WaitingForApproval:')
    expect(monitorAnimationCode).toContain('case RunStatus.WaitingForAnswer:')
    expect(monitorAnimationCode).toContain('case RunStatus.Cancelling:')
    expect(monitorAnimationCode).toContain('case RunStatus.Completed when includeTerminalTransition:')
    expect(monitorAnimationCode).toContain('case RunStatus.Failed when includeTerminalTransition:')
    expect(monitorAnimationCode).toContain('case RunStatus.Interrupted when includeTerminalTransition:')
    expect(settingsView).toContain('aria-label="t(\'启用任务监视器动画\')"')
  })

  it('does not replay terminal transitions when navigating between tasks or runs', () => {
    expect(monitorCode).toContain('SetStatusBrush(projection.Status, projection.TaskId, projection.RunId)')
    expect(monitorAnimationCode).toContain('previousTaskId == taskId')
    expect(monitorAnimationCode).toContain('previousRunId == runId')
    expect(monitorAnimationCode).toContain('previousStatus != status')
    expect(monitorAnimationCode).toContain('StartStatusIndicatorAnimation(status, includeTerminalTransition)')
  })

  it('keeps transient activity status separate from the final summary', () => {
    expect(monitorCode).toContain('projection.ActivityStatus ??')
    expect(monitorView).toContain('x:Name="ActivityStatusText"')
    expect(monitorView).not.toContain('Text="总结："')
    expect(monitorView).not.toContain('Text="结果摘要"')
  })

  it('scopes Monitor status text to the current or latest run', () => {
    expect(monitorCode).toContain('HeaderStatus.Text = MonitorRunStatus(projection.Status)')
    expect(monitorCode).toContain('$"当前一轮 · {localized}"')
    expect(monitorCode).toContain('$"最近一轮：{localized}"')
    expect(monitorCode).toContain('$"Latest: {localized}"')
    expect(monitorCode).toContain('MonitorRunStatus(task.Status),')
    expect(monitorCode).toContain('workspaceLabel,')
    expect(monitorCode).toContain('DesktopLocalizer.Text("本轮任务已完成", "Run completed")')
    expect(monitorCode).toContain('DesktopLocalizer.Text("本轮任务已停止", "Run stopped")')
    expect(monitorCode).toContain('DesktopLocalizer.Text("本轮任务失败", "Run failed")')
    expect(monitorView).toContain('Text="本轮任务已完成"')
  })

  it('keeps every Monitor activity summary to one compact display line', () => {
    const activityTemplate = monitorView.match(
      /<ListBox x:Name="ActivityList"[\s\S]*?<\/ListBox\.ItemTemplate>/u,
    )?.[0]

    expect(monitorCode).toContain('BuildMonitorActivitySummaries(projection)')
    expect(monitorCode).toContain('NormalizeActivitySummary(activity.Text)')
    expect(monitorCode).toContain('StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries')
    expect(activityTemplate).toContain('TextWrapping="NoWrap"')
    expect(activityTemplate).toContain('TextTrimming="CharacterEllipsis"')
    expect(activityTemplate).toContain('ToolTip="{Binding}"')
    expect(activityTemplate).toContain('<DockPanel Margin="0,2">')
    expect(activityTemplate).toContain('<Setter Property="HorizontalContentAlignment" Value="Stretch" />')
  })

  it('shows each tool invocation once using its target instead of output or lifecycle rows', () => {
    const activityBuilder = monitorCode.match(
      /private static IReadOnlyList<string> BuildMonitorActivitySummaries[\s\S]*?private static string NormalizeActivitySummary/u,
    )?.[0]

    expect(activityBuilder).toContain('!IsToolActivity(activity.Kind)')
    expect(activityBuilder).toContain('block.Kind is TranscriptBlockKind.Tool or TranscriptBlockKind.WebSearch')
    expect(activityBuilder).toContain('Sequence: block.LastSequence')
    expect(activityBuilder).toContain('Text: BuildToolActivitySummary(block)')
    expect(activityBuilder).toContain('var target = NormalizeActivitySummary(block.Input ?? string.Empty)')
    expect(activityBuilder).toContain('DesktopLocalizer.Text("网络搜索", "Web Search")')
    expect(activityBuilder).not.toContain('block.Output')
    expect(activityBuilder).toContain('TranscriptBlockStatus.Completed => $"✓ {description}"')
    expect(activityBuilder).toContain('TranscriptBlockStatus.Failed => $"✕ {description}"')
    expect(activityBuilder).toContain('or CompanionRunEventKind.ToolProgressed')
    expect(activityBuilder).toContain('or CompanionRunEventKind.ToolCompleted')
    expect(activityBuilder).toContain('or CompanionRunEventKind.ToolFailed')
  })

  it('shows the web search query as one tool row while keeping result content out of Monitor', () => {
    expect(monitorCode).not.toContain('IsWebSearchActivity')
    expect(monitorCode).toContain('TranscriptBlockKind.WebSearch')
    expect(monitorCode).toContain('var target = NormalizeActivitySummary(block.Input ?? string.Empty)')
    expect(monitorCode).not.toContain('NormalizeActivitySummary(block.Output')
    expect(monitorCode).toContain('DesktopLocalizer.Text("网络搜索进行中", "Searching the web")')
  })

  it('shows an animated loading state only while AI summary generation is explicit', () => {
    expect(monitorCode).not.toContain('private bool _aiSummaryEnabled')
    expect(monitorCode).not.toContain('TaskSettings taskSettings')
    expect(monitorCode).toContain('BuildLatestAgentMessageSummary(projection)')
    expect(monitorCode).toContain('block.Kind == TranscriptBlockKind.AssistantMessage')
    expect(monitorCode).toContain('private const int ResultFallbackMessageLimit = 240;')
    expect(monitorCode).toContain('ResultFallbackMessageLimit - 1')
    expect(monitorCode).toContain('"…"')
    expect(monitorCode).toContain('projection?.AiSummaryStatus == AiSummaryStatus.Generating')
    expect(monitorCode).not.toContain('string.IsNullOrWhiteSpace(projection.Summary);')
    expect(monitorCode).toContain('projection.AiSummaryStatus == AiSummaryStatus.Generating')
    expect(monitorCode).toContain(': BuildLatestAgentMessageSummary(projection)')
    expect(monitorCode).toContain('ResultSummary.Visibility = !string.IsNullOrWhiteSpace(resultSummary)')
    expect(monitorCode).toContain('? Visibility.Visible')
    expect(monitorCode).toContain(': Visibility.Collapsed')
    expect(monitorView).toContain('x:Name="ResultSummaryLoading"')
    expect(monitorView).toContain('x:Name="ResultSummarySpinnerRotate"')
    expect(monitorView).toContain('Text="正在生成 AI 总结"')
    const summarySpinner = monitorView.match(
      /<Grid Width="16"[\s\S]*?x:Name="ResultSummarySpinnerRotate"[\s\S]*?<\/Grid\.RenderTransform>/u,
    )?.[0]
    expect(summarySpinner).toContain('CenterX="8"')
    expect(summarySpinner).toContain('CenterY="8"')
    expect(summarySpinner).not.toContain('RenderTransformOrigin=')
    expect(monitorAnimationCode).toContain('UpdateAiSummaryLoadingState')
    expect(monitorAnimationCode).toContain('RepeatBehavior = RepeatBehavior.Forever')
    expect(desktopLocalizer).toContain('["正在生成 AI 总结"] = "Generating AI summary"')
    expect(monitorView).not.toContain('ResultSummaryExpand')
    expect(monitorView).not.toContain('ResultSummaryToggle')
  })

  it('lets the result card fit its content up to a maximum height', () => {
    const resultPanel = monitorView.match(/<Border x:Name="ResultPanel"[\s\S]*?<\/Border>/u)?.[0]

    expect(resultPanel).toContain('MaxHeight="360"')
    expect(resultPanel).toContain('VerticalAlignment="Top"')
    expect(resultPanel).not.toContain('MinHeight=')
    expect(resultPanel).toContain('<ScrollViewer Grid.Row="1" Margin="0,14,0,0"')
    expect(monitorView.indexOf('x:Name="ResultTitle"')).toBeLessThan(
      monitorView.indexOf('<ScrollViewer Grid.Row="1" Margin="0,14,0,0"'),
    )
    expect(monitorView).not.toContain('最近授权与回答')
    expect(monitorView).toContain('x:Name="ResultSummary"')
    expect(monitorView).not.toContain('x:Name="ResultSummary" Margin="0,14,0,0" TextWrapping="Wrap"\n                                           Text="任务完成"')
  })

  it('lets the expanded Monitor window follow content height up to its maximum', () => {
    const expandHandler = monitorCode.match(
      /private void Expand\(\)[\s\S]*?private void Collapse\(\)/u,
    )?.[0]
    const collapseHandler = monitorCode.match(
      /private void Collapse\(\)[\s\S]*?private void OnMonitorSizeChanged/u,
    )?.[0]

    expect(monitorCode).toContain('private const double ExpandedMaximumHeight = 620;')
    expect(expandHandler).toContain('MaxHeight = ExpandedMaximumHeight;')
    expect(expandHandler).toContain('Height = double.NaN;')
    expect(expandHandler).toContain('SizeToContent = SizeToContent.Height;')
    expect(expandHandler).not.toMatch(/^\s*Height = ExpandedMaximumHeight;$/mu)
    expect(collapseHandler).toContain('SizeToContent = SizeToContent.Manual;')
    expect(collapseHandler).toContain('ClearValue(MaxHeightProperty);')
    expect(collapseHandler).toContain('Height = CapsuleHeight;')
    expect(expandHandler).toContain('ExpandedBody.Visibility = Visibility.Visible;')
    expect(collapseHandler).toContain('ExpandedBody.Visibility = Visibility.Collapsed;')
    expect(monitorCode).toContain('UpdateExpandedHeaderState();')
    expect(monitorCode).toContain('SizeChanged += OnMonitorSizeChanged;')
    expect(monitorCode).toContain('WindowPlacementService.PlaceAtCorner(this, _settings.Position);')
  })

  it('summarizes completed work and keeps recent interactions to one line each', () => {
    expect(monitorView).toContain('x:Name="ResultThinkingCount"')
    expect(monitorView).toContain('x:Name="ResultToolCount"')
    expect(monitorView).toContain('x:Name="ResultWebSearchCount"')
    expect(monitorCode).toContain('block.Kind == TranscriptBlockKind.WebSearch')
    expect(monitorCode).toContain('ResultActivityCounts.Visibility')
    expect(monitorCode).toContain('thinkingCount > 0 ? Visibility.Visible : Visibility.Collapsed')
    expect(desktopLocalizer).toContain('["网络搜索"] = "Web searches"')
    expect(desktopLocalizer).not.toContain('AI summaries are disabled')
    expect(monitorView).toContain('x:Name="ResultInteractionList"')
    expect(monitorView).toContain('TextWrapping="NoWrap"')
    expect(monitorView).toContain('TextTrimming="CharacterEllipsis"')
    expect(monitorCode).toContain('private const int ResultInteractionLimit = 3;')
    expect(monitorCode).toContain('.Take(ResultInteractionLimit)')
  })

  it('shows resolved interactions as compact results without repeating their prompts', () => {
    const summaryFormatter = monitorCode.match(
      /private static string BuildResultInteractionSummary[\s\S]*?private static string NormalizeSingleLine/u,
    )?.[0]

    expect(summaryFormatter).toContain('interaction.InteractionKind == "Question"')
    expect(summaryFormatter).toContain('? BuildAnswerSummary(interaction)')
    expect(summaryFormatter).toContain(': BuildApprovalSummary(interaction)')
    expect(summaryFormatter).toContain('FirstNonEmptyLine(interaction.Content)')
    expect(summaryFormatter).toContain('TranscriptBlockStatus.Completed => DesktopLocalizer.Text("已允许", "Allowed")')
    expect(summaryFormatter).toContain('TranscriptBlockStatus.Cancelled => DesktopLocalizer.Text("已拒绝", "Denied")')
    expect(summaryFormatter).toContain('var answer = NormalizeSingleLine(interaction.Output ?? string.Empty)')
    expect(summaryFormatter).toContain('interaction.Status == TranscriptBlockStatus.Completed && !string.IsNullOrWhiteSpace(answer)')
    expect(summaryFormatter).toContain('TranscriptBlockStatus.Cancelled => DesktopLocalizer.Text("已取消", "Cancelled")')
    expect(summaryFormatter).not.toContain('NormalizeSingleLine(interaction.Content)')
    expect(summaryFormatter).not.toContain('$"{prompt} · {outcome}"')
  })

  it('uses localized task-direction controls without a separate mode label', () => {
    expect(monitorView).toContain('x:Name="DirectionPlaceholderText"')
    expect(monitorView).not.toContain('x:Name="DirectionModeText"')
    expect(monitorCode).toContain('DesktopLocalizer.Text("立即调整", "Steer now")')
    expect(monitorCode).toContain('DesktopLocalizer.Text("发送新一轮", "Start new run")')
    expect(monitorCode).toContain('DesktopLocalizer.Text("在智能体对话中打开 ↗", "Open in Agent Chat ↗")')
  })

  it('keeps answer controls cancellable and the choice dropdown open while it is in use', () => {
    expect(monitorView.match(/Content="取消"[\s\S]*?Click="OnCancelInteractionClick"/gu)).toHaveLength(2)
    expect(monitorCode).toContain('private async void OnCancelInteractionClick')
    expect(monitorCode).toContain('FindVisualAncestor<System.Windows.Controls.ComboBox>(source)')
    expect(monitorCode).toContain('FindVisualAncestor<System.Windows.Controls.ComboBoxItem>(source)')
    expect(monitorCode).toContain('InteractionOptionsComboBox.IsDropDownOpen')
    expect(monitorCode).toContain('HasActiveInputFocus()')
    expect(monitorView).toContain('x:Name="InteractionCustomResponseTextBox"')
    expect(monitorView).toContain('SelectionChanged="OnInteractionOptionChanged"')
    expect(monitorCode).toContain('InteractionCustomResponseTextBox.Visibility = isCustomAnswer')
  })
})
