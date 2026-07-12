using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PyRuntimeInspector.App.Infrastructure;
using PyRuntimeInspector.App.Services;
using PyRuntimeInspector.App.ViewModels;
using Xunit;

namespace PyRuntimeInspector.App.Tests;

[Collection("WPF UI")]
public sealed class MainWindowSmokeTests
{
    [Fact]
    public async Task MainWindowAtMinimumViewportRendersWithoutBindingErrorsAndVirtualizesVariables()
    {
        using var settingsDirectory = new TemporaryDirectory();
        var testSettingsPath = Path.Combine(settingsDirectory.Path, "settings.json");
        var userSettingsPath = new JsonAppSettingsService().SettingsPath;
        var userSettingsBefore = CaptureFile(userSettingsPath);
        Assert.False(string.Equals(testSettingsPath, userSettingsPath, StringComparison.OrdinalIgnoreCase));

        var completed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            App? application = null;
            MainWindow? window = null;
            var bindingSource = PresentationTraceSources.DataBindingSource;
            var originalBindingTraceLevel = bindingSource.Switch.Level;
            using var bindingFailures = new BindingFailureTraceListener();
            try
            {
                bindingSource.Switch.Level = SourceLevels.Warning;
                bindingSource.Listeners.Add(bindingFailures);

                application = new App();
                application.InitializeComponent();
                var updateManager = new AppUpdateManager(new AvailableUpdateService());
                window = new MainWindow(
                    new JsonAppSettingsService(testSettingsPath),
                    updateManager,
                    new NoOpUpdateInstallerLauncher())
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = 0,
                    Top = 0,
                    Width = 960,
                    Height = 540,
                };
                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                Assert.InRange(window.ActualWidth, 950, 970);
                Assert.InRange(window.ActualHeight, 530, 550);
                var updateBanner = Descendants<Border>(window)
                    .Single(border => AutomationProperties.GetName(border) == "PyMonitor update available");
                Assert.Equal(Visibility.Visible, updateBanner.Visibility);
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(updateBanner));
                var updateAction = Descendants<Button>(updateBanner)
                    .Single(button => AutomationProperties.GetName(button) == "Download and install PyMonitor update");
                AssertElementIsVisibleAndInside(updateAction, window, "Update action", "minimum supported viewport");
                var primaryActions = Descendants<Button>(window)
                    .Where(button => button.Content is string text
                        && text is "Quick Attach" or "Refresh" or "Detach" or "Help" or "About")
                    .ToDictionary(button => (string)button.Content);
                Assert.Equal(5, primaryActions.Count);
                foreach (var (name, button) in primaryActions)
                {
                    AssertElementIsVisibleAndInside(button, window, name, "minimum supported viewport");
                }

                var recoveryBanner = Descendants<Border>(window)
                    .Single(border => AutomationProperties.GetName(border) == "Quick Attach recovery instructions");
                var recoveryVisibility = Assert.IsType<Binding>(BindingOperations.GetBindingBase(
                    recoveryBanner,
                    UIElement.VisibilityProperty));
                Assert.Equal("HasConnectionRecovery", recoveryVisibility.Path.Path);
                Assert.Equal(Visibility.Collapsed, recoveryBanner.Visibility);
                Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(recoveryBanner));
                var recoveryHelpText = Assert.IsType<Binding>(BindingOperations.GetBindingBase(
                    recoveryBanner,
                    AutomationProperties.HelpTextProperty));
                Assert.Equal("ConnectionRecoveryMessage", recoveryHelpText.Path.Path);
                var recoveryText = Descendants<TextBlock>(recoveryBanner)
                    .Single(text => BindingOperations.GetBindingBase(text, TextBlock.TextProperty)
                        is Binding { Path.Path: "ConnectionRecoveryMessage" });
                Assert.NotNull(recoveryText);

                var helpButton = primaryActions["Help"];
                Assert.Equal("Open PyMonitor help", AutomationProperties.GetName(helpButton));
                Assert.Contains("F1", helpButton.ToolTip?.ToString(), StringComparison.OrdinalIgnoreCase);
                Assert.Same(MainWindow.OpenHelpCommand, helpButton.Command);
                Assert.Contains(MainWindow.OpenHelpCommand.InputGestures.OfType<KeyGesture>(), gesture =>
                    gesture.Key == Key.F1 && gesture.Modifiers == ModifierKeys.None);
                Assert.Contains(window.CommandBindings.Cast<CommandBinding>(), binding =>
                    binding.Command == MainWindow.OpenHelpCommand);

                MainWindow.OpenHelpCommand.Execute(null, window);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var helpWindow = Assert.Single(window.OwnedWindows.OfType<HelpWindow>());
                helpWindow.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                helpWindow.UpdateLayout();
                Assert.True(helpWindow.IsVisible);
                Assert.True(window.IsEnabled, "The Help window must be modeless so the main window remains usable.");
                Assert.Same(window, helpWindow.Owner);

                MainWindow.OpenHelpCommand.Execute(null, window);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Same(helpWindow, Assert.Single(window.OwnedWindows.OfType<HelpWindow>()));

                helpWindow.Width = Math.Max(helpWindow.MinWidth, 720);
                helpWindow.Height = Math.Max(helpWindow.MinHeight, 500);
                helpWindow.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                helpWindow.UpdateLayout();

                var helpSearch = LogicalDescendants<TextBox>(helpWindow)
                    .Single(textBox => AutomationProperties.GetName(textBox) == "Search help");
                var helpResults = LogicalDescendants<ListBox>(helpWindow)
                    .Single(listBox => AutomationProperties.GetName(listBox) == "Help search results");
                var helpArticle = LogicalDescendants<FrameworkElement>(helpWindow)
                    .Single(element => AutomationProperties.GetName(element) == "Selected help article");
                var helpResultSummary = Assert.IsType<TextBlock>(helpWindow.FindName("ResultSummaryText"));
                var noHelpResults = LogicalDescendants<Border>(helpWindow)
                    .Single(border => AutomationProperties.GetName(border) == "No help search results");
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(helpResultSummary));
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(noHelpResults));
                Assert.Equal(helpResultSummary.Text, AutomationProperties.GetName(helpResultSummary));
                Assert.Contains(HelpWindow.FocusSearchCommand.InputGestures.OfType<KeyGesture>(), gesture =>
                    gesture.Key == Key.F1 && gesture.Modifiers == ModifierKeys.None);
                Assert.Contains(HelpWindow.FocusSearchCommand.InputGestures.OfType<KeyGesture>(), gesture =>
                    gesture.Key == Key.F && gesture.Modifiers == ModifierKeys.Control);
                Assert.Contains(helpWindow.CommandBindings.Cast<CommandBinding>(), binding =>
                    binding.Command == HelpWindow.FocusSearchCommand);
                AssertElementIsVisibleAndInside(helpSearch, helpWindow, "Help search", "minimum Help window");
                AssertElementIsVisibleAndInside(helpResults, helpWindow, "Help results", "minimum Help window");
                AssertElementIsVisibleAndInside(helpArticle, helpWindow, "Help article", "minimum Help window");

                helpSearch.Text = "DataFrame";
                helpSearch.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                helpWindow.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                helpWindow.UpdateLayout();
                var helpViewModel = Assert.IsType<HelpViewModel>(helpWindow.DataContext);
                var dataFrameTopic = Assert.Single(helpViewModel.FilteredTopics,
                    topic => topic.Id == "dataframes");
                Assert.Equal(helpViewModel.ResultSummary, AutomationProperties.GetName(helpResultSummary));
                helpResults.SelectedItem = dataFrameTopic;
                helpWindow.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                helpWindow.UpdateLayout();
                Assert.Same(dataFrameTopic, helpViewModel.SelectedTopic);
                var helpArticleText = string.Join(Environment.NewLine,
                    LogicalDescendants<TextBlock>(helpArticle).Select(text => text.Text)
                        .Concat(LogicalDescendants<TextBox>(helpArticle).Select(text => text.Text)));
                Assert.Contains("DataFrame", helpArticleText, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(dataFrameTopic.Example, helpArticleText, StringComparison.Ordinal);
                helpWindow.Close();

                var objectNavigationButtons = Descendants<Button>(window)
                    .Where(button => AutomationProperties.GetName(button) is
                        "Back in object history" or "Forward in object history" or "Go to parent object")
                    .ToArray();
                Assert.Equal(3, objectNavigationButtons.Length);
                Assert.All(objectNavigationButtons, button =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(button.ToolTip?.ToString()));
                    Assert.Equal(button.ToolTip?.ToString(), AutomationProperties.GetHelpText(button));
                    var presenters = Descendants<ContentPresenter>(button).ToArray();
                    Assert.NotEmpty(presenters);
                    Assert.All(presenters, presenter =>
                        Assert.False(presenter.RecognizesAccessKey));
                });
                var ancestry = Descendants<ItemsControl>(window)
                    .Single(control => AutomationProperties.GetName(control) == "Object ancestry breadcrumb");
                Assert.NotNull(ancestry.ItemTemplate);
                Assert.Equal("History 0 / 0", Descendants<TextBlock>(window)
                    .Single(text => AutomationProperties.GetName(text) == "History 0 / 0").Text);
                Assert.Equal("No level", Descendants<TextBlock>(window)
                    .Single(text => AutomationProperties.GetName(text) == "No level").Text);

                var objectSearchNames = LogicalDescendants<TextBox>(window)
                    .Select(AutomationProperties.GetName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToHashSet(StringComparer.Ordinal);
                Assert.Contains("Search immediate child names", objectSearchNames);
                Assert.Contains("Search loaded Object Tree names", objectSearchNames);
                var immediateChildren = LogicalDescendants<DataGrid>(window)
                    .Single(grid => AutomationProperties.GetName(grid) == "Immediate object children");
                var immediateChildrenBinding = Assert.IsType<Binding>(BindingOperations
                    .GetBindingBase(immediateChildren, ItemsControl.ItemsSourceProperty));
                Assert.Equal("FilteredObjectChildren", immediateChildrenBinding.Path.Path);
                var objectTree = LogicalDescendants<TreeView>(window)
                    .Single(tree => AutomationProperties.GetName(tree) == "Object member tree");
                Assert.True(VirtualizingPanel.GetIsVirtualizing(objectTree));
                Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(objectTree));
                Assert.True(ScrollViewer.GetCanContentScroll(objectTree));
                Assert.Equal(ScrollBarVisibility.Auto, ScrollViewer.GetHorizontalScrollBarVisibility(objectTree));
                Assert.Contains(objectTree.ItemContainerStyle.Setters.OfType<Setter>(), setter =>
                    setter.Property == UIElement.VisibilityProperty
                    && setter.Value is Binding { Path.Path: "IsSearchVisible" });
                Assert.Contains(objectTree.ItemContainerStyle.Setters.OfType<Setter>(), setter =>
                    setter.Property == Control.HorizontalContentAlignmentProperty
                    && Equals(setter.Value, HorizontalAlignment.Stretch));
                var objectItemNameSetter = Assert.Single(
                    objectTree.ItemContainerStyle.Setters.OfType<Setter>(),
                    setter => setter.Property == AutomationProperties.NameProperty);
                Assert.Equal("AccessibilityName", Assert.IsType<Binding>(objectItemNameSetter.Value).Path.Path);
                var objectItemHelpSetter = Assert.Single(
                    objectTree.ItemContainerStyle.Setters.OfType<Setter>(),
                    setter => setter.Property == AutomationProperties.HelpTextProperty);
                Assert.Equal("HierarchyHelpText", Assert.IsType<Binding>(objectItemHelpSetter.Value).Path.Path);
                var accessibleObjectNode = new ObjectTreeNode { Label = "root" };
                var accessibleObjectItem = new TreeViewItem
                {
                    DataContext = accessibleObjectNode,
                    Style = objectTree.ItemContainerStyle,
                };
                accessibleObjectItem.ApplyTemplate();
                var objectItemPeer = new TreeViewItemAutomationPeer(accessibleObjectItem);
                Assert.Equal(accessibleObjectNode.AccessibilityName, objectItemPeer.GetName());
                Assert.Equal(accessibleObjectNode.HierarchyHelpText, objectItemPeer.GetHelpText());
                var objectTemplate = Assert.Single(
                    objectTree.Resources.Values.OfType<HierarchicalDataTemplate>());
                var objectHierarchyRow = Assert.IsType<Border>(objectTemplate.LoadContent());
                Assert.Equal("ObjectHierarchyRow", objectHierarchyRow.Name);
                Assert.Null(BindingOperations.GetBindingBase(objectHierarchyRow, AutomationProperties.NameProperty));
                Assert.Null(BindingOperations.GetBindingBase(objectHierarchyRow, AutomationProperties.HelpTextProperty));
                objectHierarchyRow.DataContext = new ObjectTreeNode
                {
                    Label = new string('o', 250),
                    Path = new string('p', 250),
                };
                objectHierarchyRow.Measure(new Size(330, double.PositiveInfinity));
                Assert.InRange(objectHierarchyRow.DesiredSize.Width, 0, 330);
                var objectConnector = LogicalDescendants<Grid>(objectHierarchyRow)
                    .Single(grid => grid.Name == "ObjectHierarchyConnector");
                Assert.False(objectConnector.IsHitTestVisible);
                Assert.Contains(objectConnector.Style.Triggers.OfType<DataTrigger>(), trigger =>
                    trigger.Binding is Binding { Path.Path: "Parent" }
                    && trigger.Value is null);
                var objectBadge = LogicalDescendants<Border>(objectHierarchyRow)
                    .Single(border => border.Name == "ObjectHierarchyBadge");
                Assert.Equal(VerticalAlignment.Top, objectBadge.VerticalAlignment);
                Assert.Contains(objectBadge.Style.Triggers.OfType<DataTrigger>(), trigger =>
                    trigger.Binding is Binding { Path.Path: "Kind" }
                    && Equals(trigger.Value, ObjectNodeKind.Group));
                var objectBadgeText = LogicalDescendants<TextBlock>(objectBadge)
                    .Single(text => text.Style.Setters.OfType<Setter>().Any(setter =>
                        setter.Property == TextBlock.TextProperty
                        && setter.Value is Binding { Path.Path: "LevelLabel" }));
                Assert.Contains(objectBadgeText.Style.Triggers.OfType<DataTrigger>(), trigger =>
                    trigger.Binding is Binding { Path.Path: "Kind" }
                    && Equals(trigger.Value, ObjectNodeKind.Group)
                    && trigger.Setters.OfType<Setter>().Any(setter =>
                        setter.Property == TextBlock.TextProperty
                        && Equals(setter.Value, "GROUP")));
                Assert.Contains(LogicalDescendants<TextBlock>(objectHierarchyRow), text =>
                    BindingOperations.GetBindingBase(text, TextBlock.TextProperty)
                        is Binding { Path.Path: "ParentContext" });
                Assert.Contains(objectHierarchyRow.Style.Triggers.OfType<DataTrigger>(), trigger =>
                    trigger.Binding is Binding { Path.Path: "IsSearchAncestor" });
                Assert.Contains(objectHierarchyRow.Style.Triggers.OfType<DataTrigger>(), trigger =>
                    trigger.Binding is Binding { Path.Path: "IsSearchMatch" });

                var classTab = LogicalDescendants<TabItem>(window)
                    .Single(tab => string.Equals(tab.Header?.ToString(), "Class and Methods", StringComparison.Ordinal));
                classTab.IsSelected = true;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                var classSearch = LogicalDescendants<TextBox>(classTab)
                    .Single(textBox => AutomationProperties.GetName(textBox)
                        == "Search class members methods and parameters");
                var classSearchToolbar = LogicalDescendants<Border>(classTab)
                    .Single(border => AutomationProperties.GetName(border) == "Class member search toolbar");
                var classSearchLabel = LogicalDescendants<TextBlock>(classSearchToolbar)
                    .Single(text => AutomationProperties.GetName(text) == "Find class members");
                Assert.Equal("Find members", classSearchLabel.Text);
                Assert.Same(classSearchLabel, AutomationProperties.GetLabeledBy(classSearch));
                Assert.Equal(31, classSearch.Height);
                Assert.Equal(VerticalAlignment.Center, classSearch.VerticalAlignment);
                Assert.Equal(VerticalAlignment.Center, classSearch.VerticalContentAlignment);
                var classSearchBinding = Assert.IsType<Binding>(BindingOperations
                    .GetBindingBase(classSearch, TextBox.TextProperty));
                Assert.Equal("ClassTreeSearchText", classSearchBinding.Path.Path);
                Assert.Equal(UpdateSourceTrigger.PropertyChanged, classSearchBinding.UpdateSourceTrigger);
                var searchScope = classSearch.ToolTip?.ToString() ?? "";
                Assert.Equal(searchScope, AutomationProperties.GetHelpText(classSearch));
                Assert.Contains("already loaded", searchScope, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("class details", searchScope, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("safety limit", searchScope, StringComparison.OrdinalIgnoreCase);
                foreach (var expectedScope in new[]
                {
                    "name", "kind", "declaring class", "signature", "detail", "source", "parameter",
                })
                {
                    Assert.Contains(expectedScope, searchScope, StringComparison.OrdinalIgnoreCase);
                }

                var clearClassSearch = LogicalDescendants<Button>(classTab)
                    .Single(button => AutomationProperties.GetName(button) == "Clear class member search");
                var classViewModel = Assert.IsType<MainViewModel>(window.DataContext);
                Assert.Same(classViewModel.ClearClassTreeSearchCommand, clearClassSearch.Command);
                Assert.Equal(VerticalAlignment.Center, clearClassSearch.VerticalAlignment);
                Assert.True(clearClassSearch.MinHeight >= 31);
                var classSummary = LogicalDescendants<TextBlock>(classTab)
                    .Single(text => BindingOperations.GetBindingBase(text, TextBlock.TextProperty)
                        is Binding { Path.Path: "ClassSummary" });
                var classTree = LogicalDescendants<TreeView>(classTab)
                    .Single(tree => AutomationProperties.GetName(tree) == "Class members methods and parameters");
                Assert.Equal(0, Grid.GetRow(classSummary));
                Assert.Equal(1, Grid.GetRow(classSearchToolbar));

                var originalClassSummary = classSummary.Text;
                var originalSearchWidth = classSearch.ActualWidth;
                var originalTreeHeight = classTree.ActualHeight;
                var originalToolbarBounds = classSearchToolbar.TransformToAncestor(window).TransformBounds(
                    new Rect(0, 0, classSearchToolbar.ActualWidth, classSearchToolbar.ActualHeight));
                classSummary.SetCurrentValue(TextBlock.TextProperty, string.Join(Environment.NewLine, Enumerable.Repeat(
                    "A deliberately long class description that must not stretch the search field.", 10)));
                window.UpdateLayout();
                Assert.Contains("deliberately long class description", classSummary.Text, StringComparison.Ordinal);
                Assert.InRange(classSummary.ActualHeight, 15.5, 16.5);
                Assert.InRange(classSearch.ActualHeight, 30.5, 31.5);
                Assert.InRange(classSearchToolbar.ActualHeight, 31, 42);
                var summaryBounds = classSummary.TransformToAncestor(window).TransformBounds(
                    new Rect(0, 0, classSummary.ActualWidth, classSummary.ActualHeight));
                var toolbarBounds = classSearchToolbar.TransformToAncestor(window).TransformBounds(
                    new Rect(0, 0, classSearchToolbar.ActualWidth, classSearchToolbar.ActualHeight));
                Assert.True(toolbarBounds.Top >= summaryBounds.Bottom,
                    $"Class search toolbar overlaps the description: summary={summaryBounds}, toolbar={toolbarBounds}.");
                Assert.InRange(Math.Abs(toolbarBounds.Top - originalToolbarBounds.Top), 0, 0.5);
                Assert.InRange(Math.Abs(classSearch.ActualWidth - originalSearchWidth), 0, 0.5);
                Assert.InRange(Math.Abs(classTree.ActualHeight - originalTreeHeight), 0, 0.5);
                Assert.NotNull(BindingOperations.GetBindingBase(classSummary, TextBlock.TextProperty));
                classSummary.SetCurrentValue(TextBlock.TextProperty, originalClassSummary);
                window.UpdateLayout();
                var classSearchStatus = LogicalDescendants<TextBlock>(classTab)
                    .Single(text => AutomationProperties.GetLiveSetting(text) == AutomationLiveSetting.Polite);
                var classStatusBinding = Assert.IsType<Binding>(BindingOperations
                    .GetBindingBase(classSearchStatus, TextBlock.TextProperty));
                Assert.Equal("ClassTreeSearchResultLabel", classStatusBinding.Path.Path);
                Assert.True(classStatusBinding.NotifyOnTargetUpdated);
                Assert.Equal(classSearchStatus.Text, AutomationProperties.GetName(classSearchStatus));
                Assert.NotNull(typeof(MainWindow).GetMethod(
                    "ClassTreeSearchStatus_TargetUpdated",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
                var classStatusTargetUpdates = 0;
                classSearchStatus.TargetUpdated += (_, _) => classStatusTargetUpdates++;

                var classItemsBinding = Assert.IsType<Binding>(BindingOperations
                    .GetBindingBase(classTree, ItemsControl.ItemsSourceProperty));
                Assert.Equal("ClassTree", classItemsBinding.Path.Path);
                Assert.True(VirtualizingPanel.GetIsVirtualizing(classTree));
                Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(classTree));
                Assert.True(ScrollViewer.GetCanContentScroll(classTree));
                Assert.Equal(ScrollBarVisibility.Disabled, ScrollViewer.GetHorizontalScrollBarVisibility(classTree));
                var classExpandedSetter = Assert.Single(
                    classTree.ItemContainerStyle.Setters.OfType<Setter>(),
                    setter => setter.Property == TreeViewItem.IsExpandedProperty);
                var classExpandedBinding = Assert.IsType<Binding>(classExpandedSetter.Value);
                Assert.Equal("IsExpanded", classExpandedBinding.Path.Path);
                Assert.Equal(BindingMode.TwoWay, classExpandedBinding.Mode);
                Assert.Contains(classTree.ItemContainerStyle.Setters.OfType<Setter>(), setter =>
                    setter.Property == UIElement.VisibilityProperty
                    && setter.Value is Binding { Path.Path: "IsSearchVisible" });
                Assert.Contains(classTree.ItemContainerStyle.Setters.OfType<Setter>(), setter =>
                    setter.Property == Control.HorizontalContentAlignmentProperty
                    && Equals(setter.Value, HorizontalAlignment.Stretch));
                var classItemNameSetter = Assert.Single(
                    classTree.ItemContainerStyle.Setters.OfType<Setter>(),
                    setter => setter.Property == AutomationProperties.NameProperty);
                Assert.Equal("AccessibilityName", Assert.IsType<Binding>(classItemNameSetter.Value).Path.Path);
                var classItemHelpSetter = Assert.Single(
                    classTree.ItemContainerStyle.Setters.OfType<Setter>(),
                    setter => setter.Property == AutomationProperties.HelpTextProperty);
                Assert.Equal("HierarchyHelpText", Assert.IsType<Binding>(classItemHelpSetter.Value).Path.Path);
                var accessibleClassNode = new ClassTreeNode { Label = "Methods", Kind = "group" };
                var accessibleClassItem = new TreeViewItem
                {
                    DataContext = accessibleClassNode,
                    Style = classTree.ItemContainerStyle,
                };
                accessibleClassItem.ApplyTemplate();
                var classItemPeer = new TreeViewItemAutomationPeer(accessibleClassItem);
                Assert.Equal(accessibleClassNode.AccessibilityName, classItemPeer.GetName());
                Assert.Equal(accessibleClassNode.HierarchyHelpText, classItemPeer.GetHelpText());

                var classTemplate = Assert.Single(
                    classTree.Resources.Values.OfType<HierarchicalDataTemplate>());
                var classHierarchyRow = Assert.IsType<Border>(classTemplate.LoadContent());
                var classTemplateRoot = Assert.IsAssignableFrom<DependencyObject>(classHierarchyRow);
                Assert.Equal("ClassHierarchyRow", classHierarchyRow.Name);
                Assert.Null(BindingOperations.GetBindingBase(classHierarchyRow, AutomationProperties.NameProperty));
                Assert.Null(BindingOperations.GetBindingBase(classHierarchyRow, AutomationProperties.HelpTextProperty));
                classHierarchyRow.DataContext = new ClassTreeNode
                {
                    Label = new string('c', 250),
                    Kind = new string('k', 80),
                    DeclaredBy = new string('d', 250),
                    Detail = new string('x', 500),
                    Source = new string('s', 250),
                };
                classHierarchyRow.Measure(new Size(330, double.PositiveInfinity));
                Assert.InRange(classHierarchyRow.DesiredSize.Width, 0, 330);
                var classConnector = LogicalDescendants<Grid>(classHierarchyRow)
                    .Single(grid => grid.Name == "ClassHierarchyConnector");
                Assert.False(classConnector.IsHitTestVisible);
                Assert.Contains(classConnector.Style.Triggers.OfType<DataTrigger>(), trigger =>
                    trigger.Binding is Binding { Path.Path: "Parent" }
                    && trigger.Value is null);
                var classConnectorArm = LogicalDescendants<Border>(classConnector)
                    .Single(border => border.Height == 1);
                Assert.Equal(VerticalAlignment.Top, classConnectorArm.VerticalAlignment);
                Assert.Equal(9, classConnectorArm.Margin.Top);
                var classBadge = LogicalDescendants<Border>(classHierarchyRow)
                    .Single(border => border.Name == "ClassHierarchyBadge");
                Assert.Equal(VerticalAlignment.Top, classBadge.VerticalAlignment);
                var classBadgeText = LogicalDescendants<TextBlock>(classBadge)
                    .Single(text => BindingOperations.GetBindingBase(text, TextBlock.TextProperty)
                        is Binding { Path.Path: "LevelLabel" });
                Assert.Equal("LevelLabel", Assert.IsType<Binding>(BindingOperations.GetBindingBase(
                    classBadgeText,
                    TextBlock.TextProperty)).Path.Path);
                Assert.Contains(LogicalDescendants<TextBlock>(classHierarchyRow), text =>
                    BindingOperations.GetBindingBase(text, TextBlock.TextProperty)
                        is Binding { Path.Path: "ParentContext" });
                Assert.Contains(classHierarchyRow.Style.Triggers.OfType<DataTrigger>(), trigger =>
                    trigger.Binding is Binding { Path.Path: "IsSearchAncestor" });
                Assert.Contains(classHierarchyRow.Style.Triggers.OfType<DataTrigger>(), trigger =>
                    trigger.Binding is Binding { Path.Path: "IsSearchMatch" });
                var classLabel = LogicalDescendants<TextBlock>(classTemplateRoot)
                    .Single(text => BindingOperations.GetBindingBase(text, TextBlock.TextProperty)
                        is Binding { Path.Path: "Label" });
                var directMatchTrigger = Assert.Single(
                    classLabel.Style.Triggers.OfType<DataTrigger>(),
                    trigger => trigger.Binding is Binding { Path.Path: "IsSearchMatch" });
                Assert.Contains(directMatchTrigger.Setters.OfType<Setter>(), setter =>
                    setter.Property == TextBlock.ForegroundProperty);
                Assert.Contains(directMatchTrigger.Setters.OfType<Setter>(), setter =>
                    setter.Property == TextBlock.TextDecorationsProperty);
                Assert.DoesNotContain(classLabel.Style.Triggers.OfType<DataTrigger>(), trigger =>
                    trigger.Binding is Binding { Path.Path: "IsSearchAncestor" });
                var classDetail = LogicalDescendants<TextBlock>(classTemplateRoot)
                    .Single(text => BindingOperations.GetBindingBase(text, TextBlock.TextProperty)
                        is Binding { Path.Path: "Detail" });
                Assert.Equal("Detail", Assert.IsType<Binding>(BindingOperations.GetBindingBase(
                    classDetail,
                    FrameworkElement.ToolTipProperty)).Path.Path);

                var noClassResults = LogicalDescendants<Border>(classTab)
                    .Single(border => AutomationProperties.GetName(border) == "No class member search results");
                var emptyClassBinding = Assert.IsType<Binding>(BindingOperations
                    .GetBindingBase(noClassResults, UIElement.VisibilityProperty));
                Assert.Equal("IsClassTreeSearchEmpty", emptyClassBinding.Path.Path);
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(noClassResults));
                var emptyClassText = string.Join(" ", LogicalDescendants<TextBlock>(noClassResults)
                    .Select(text => text.Text));
                Assert.Contains("loaded", emptyClassText, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("class details", emptyClassText, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("safety limit", emptyClassText, StringComparison.OrdinalIgnoreCase);

                AssertElementIsVisibleAndInside(classSearch, window, "Class search", "minimum supported viewport");
                AssertElementIsVisibleAndInside(classSearchLabel, window, "Class search label", "minimum supported viewport");
                AssertElementIsVisibleAndInside(classSearchToolbar, window, "Class search toolbar", "minimum supported viewport");
                AssertElementIsVisibleAndInside(clearClassSearch, window, "Clear class search", "minimum supported viewport");
                AssertElementIsVisibleAndInside(classSearchStatus, window, "Class search status", "minimum supported viewport");
                AssertElementIsVisibleAndInside(classTree, window, "Class member tree", "minimum supported viewport");

                classSearch.Text = "no-such-class-member";
                classSearch.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                Assert.True(noClassResults.IsVisible);
                AssertElementIsVisibleAndInside(noClassResults, window, "Empty class search", "minimum supported viewport");
                Assert.Contains("0", classSearchStatus.Text, StringComparison.Ordinal);
                Assert.True(classStatusTargetUpdates > 0,
                    "Class search status binding did not raise TargetUpdated for the live-region handler.");
                Assert.Equal(classSearchStatus.Text, AutomationProperties.GetName(classSearchStatus));
                clearClassSearch.Command.Execute(null);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal("", classViewModel.ClassTreeSearchText);
                Assert.False(noClassResults.IsVisible);

                var copyableValue = Descendants<TextBlock>(window)
                    .First(text => text.Text == "No object selected");
                copyableValue.RaiseEvent(new MouseButtonEventArgs(
                    Mouse.PrimaryDevice,
                    Environment.TickCount,
                    MouseButton.Right)
                {
                    RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent,
                });
                var copyMenu = Assert.IsType<ContextMenu>(copyableValue.ContextMenu);
                var copyItem = Assert.Single(copyMenu.Items.OfType<MenuItem>(), item =>
                    Equals(item.Header, "Copy value"));
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.True(copyMenu.IsOpen);
                Assert.True(copyItem.IsEnabled);
                copyMenu.IsOpen = false;

                var aboutWindow = new AboutWindow(
                    updateManager,
                    _ => { },
                    (_, _) => Task.CompletedTask)
                {
                    Owner = window,
                };
                aboutWindow.Show();
                aboutWindow.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var updateStatus = LogicalDescendants<TextBlock>(aboutWindow)
                    .Single(text => AutomationProperties.GetLiveSetting(text) == AutomationLiveSetting.Polite);
                Assert.Equal(updateStatus.Text, AutomationProperties.GetName(updateStatus));
                Assert.Contains("26.7.12", updateStatus.Text, StringComparison.Ordinal);
                Assert.True(LogicalDescendants<Button>(aboutWindow)
                    .Single(button => AutomationProperties.GetName(button)
                        == "Download and install available PyMonitor update")
                    .IsVisible);
                var aboutText = Descendants<TextBlock>(aboutWindow)
                    .First(text => !string.IsNullOrWhiteSpace(text.Text));
                aboutText.RaiseEvent(new MouseButtonEventArgs(
                    Mouse.PrimaryDevice,
                    Environment.TickCount,
                    MouseButton.Right)
                {
                    RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent,
                });
                var aboutMenu = Assert.IsType<ContextMenu>(aboutText.ContextMenu);
                aboutWindow.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.True(Assert.Single(aboutMenu.Items.OfType<MenuItem>()).IsEnabled);
                aboutMenu.IsOpen = false;
                aboutWindow.Close();

                var dataFrameTab = LogicalDescendants<TabItem>(window)
                    .Single(tab => string.Equals(tab.Header?.ToString(), "DataFrame", StringComparison.Ordinal));
                Assert.Equal("Pandas DataFrame preview", AutomationProperties.GetName(dataFrameTab));
                var dataFrameGrid = LogicalDescendants<DataGrid>(dataFrameTab)
                    .Single(grid => AutomationProperties.GetName(grid) == "DataFrame preview table");
                Assert.True(dataFrameGrid.AutoGenerateColumns);
                Assert.True(dataFrameGrid.EnableRowVirtualization);
                Assert.True(dataFrameGrid.EnableColumnVirtualization);
                Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(dataFrameGrid));
                var dataFrameActions = LogicalDescendants<Button>(dataFrameTab)
                    .Select(AutomationProperties.GetName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToHashSet(StringComparer.Ordinal);
                Assert.True(new[]
                {
                    "Refresh DataFrame preview",
                    "Previous DataFrame rows",
                    "Next DataFrame rows",
                    "Previous DataFrame columns",
                    "Next DataFrame columns",
                }.All(dataFrameActions.Contains));
                var viewModel = Assert.IsType<MainViewModel>(window.DataContext);
                viewModel.DataFrameColumns.Add(new DataFrameColumnInfo(0, "customer", "string"));
                using var dataFrameTable = new DataTable { Locale = System.Globalization.CultureInfo.InvariantCulture };
                dataFrameTable.Columns.Add("__index__", typeof(string));
                dataFrameTable.Columns.Add("Column_0", typeof(string));
                dataFrameTable.Rows.Add("row-1", "Ada");
                dataFrameTab.Visibility = Visibility.Visible;
                dataFrameTab.IsEnabled = true;
                dataFrameTab.IsSelected = true;
                dataFrameGrid.ItemsSource = dataFrameTable.DefaultView;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                Assert.Equal(2, dataFrameGrid.Columns.Count);
                Assert.Equal("Index", dataFrameGrid.Columns[0].Header);
                var customerHeader = Assert.IsType<StackPanel>(dataFrameGrid.Columns[1].Header);
                Assert.Equal("customer column, dtype string", AutomationProperties.GetName(customerHeader));

                var matplotlibTab = LogicalDescendants<TabItem>(window)
                    .Single(tab => string.Equals(tab.Header?.ToString(), "Matplotlib", StringComparison.Ordinal));
                Assert.Equal("Matplotlib Figure or Axes preview", AutomationProperties.GetName(matplotlibTab));
                var matplotlibRefresh = LogicalDescendants<Button>(matplotlibTab)
                    .Single(button => AutomationProperties.GetName(button) == "Refresh Matplotlib preview");
                Assert.Same(viewModel.RefreshMatplotlibCommand, matplotlibRefresh.Command);
                matplotlibTab.Visibility = Visibility.Visible;
                matplotlibTab.IsEnabled = true;
                matplotlibTab.IsSelected = true;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                Assert.True(matplotlibRefresh.IsVisible,
                    "Matplotlib refresh is not visible at the minimum supported viewport.");
                var matplotlibRefreshBounds = matplotlibRefresh.TransformToAncestor(window).TransformBounds(
                    new Rect(0, 0, matplotlibRefresh.ActualWidth, matplotlibRefresh.ActualHeight));
                Assert.True(matplotlibRefreshBounds.Left >= 0 && matplotlibRefreshBounds.Top >= 0
                    && matplotlibRefreshBounds.Right <= window.ActualWidth
                    && matplotlibRefreshBounds.Bottom <= window.ActualHeight,
                    $"Matplotlib refresh is clipped at the minimum supported viewport: {matplotlibRefreshBounds}.");
                var matplotlibImage = LogicalDescendants<Image>(matplotlibTab)
                    .Single(image => AutomationProperties.GetName(image) == "Matplotlib Figure preview");
                var matplotlibImageBinding = Assert.IsType<Binding>(BindingOperations
                    .GetBindingBase(matplotlibImage, Image.SourceProperty));
                Assert.Equal("MatplotlibPreview", matplotlibImageBinding.Path.Path);
                Assert.Contains(LogicalDescendants<TextBlock>(matplotlibTab), text =>
                    text.Text == "Showing the owning Figure.");
                Assert.Contains(LogicalDescendants<TextBlock>(matplotlibTab), text =>
                    text.Text.Contains("fig.canvas.draw()", StringComparison.Ordinal)
                    && text.Text.Contains("Refresh preview", StringComparison.Ordinal));
                Assert.Contains(LogicalDescendants<Border>(matplotlibTab), border =>
                    AutomationProperties.GetName(border) == "Matplotlib preview state");

                AssertVariablesGridIsVirtualized(window);
                Assert.True(bindingFailures.Messages.Count == 0,
                    $"WPF reported data-binding warnings/errors:{Environment.NewLine}{string.Join(Environment.NewLine, bindingFailures.Messages)}");
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                try
                {
                    window?.Close();
                    application?.Shutdown();
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }

                bindingSource.Listeners.Remove(bindingFailures);
                bindingSource.Switch.Level = originalBindingTraceLevel;
                Dispatcher.CurrentDispatcher.InvokeShutdown();
                completed.TrySetResult(failure);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var exception = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(exception);
        thread.Join(TimeSpan.FromSeconds(2));
        Assert.True(File.Exists(testSettingsPath), "Closing the smoke-test window should save only its isolated settings file.");
        Assert.NotNull(new JsonAppSettingsService(testSettingsPath).Load().LastAutomaticUpdateCheckUtc);
        Assert.Equal(userSettingsBefore, CaptureFile(userSettingsPath));
    }

    private static void AssertVariablesGridIsVirtualized(MainWindow window)
    {
        const int itemCount = 5_000;
        var variablesGrid = Descendants<DataGrid>(window)
            .Single(grid => AutomationProperties.GetName(grid) == "Variables");
        var variables = Enumerable.Range(0, itemCount)
            .Select(index => new VariableRow
            {
                Name = $"variable_{index}",
                Scope = "Local",
                HandleId = $"handle-{index}",
                TypeName = "int",
                ModuleName = "builtins",
                QualifiedTypeName = "builtins.int",
                SafePreview = index.ToString(),
                Address = $"0x{index:x}",
                ShallowSize = 28,
                ChangeToken = index.ToString(),
            })
            .ToArray();

        variablesGrid.ItemsSource = variables;
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();

        Assert.True(variablesGrid.EnableRowVirtualization);
        Assert.True(VirtualizingPanel.GetIsVirtualizing(variablesGrid));
        Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(variablesGrid));
        Assert.True(ScrollViewer.GetCanContentScroll(variablesGrid));
        Assert.True(variablesGrid.IsVisible && variablesGrid.ActualHeight > 0,
            "The Variables grid must be visible and measured at the minimum viewport.");

        var initiallyRealized = CountRealizedRows(variablesGrid, itemCount);
        AssertRealizedRowsAreBounded(initiallyRealized, itemCount, "initial viewport");
        Assert.Null(variablesGrid.ItemContainerGenerator.ContainerFromIndex(itemCount - 1));

        var firstRow = Assert.IsType<DataGridRow>(variablesGrid.ItemContainerGenerator.ContainerFromIndex(0));
        var emphasizedName = Descendants<TextBlock>(firstRow).Single(text => text.Text == "variable_0");
        Assert.Equal(FontWeights.SemiBold, emphasizedName.FontWeight);
        Assert.Equal(Application.Current.Resources["AccentBrush"], emphasizedName.Foreground);
        Assert.Equal("variable_0", emphasizedName.ToolTip);

        variablesGrid.SelectedIndex = 1;
        var firstCell = Descendants<DataGridCell>(firstRow).First();
        variablesGrid.RaiseEvent(new MouseButtonEventArgs(
            Mouse.PrimaryDevice,
            Environment.TickCount,
            MouseButton.Right)
        {
            RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent,
            Source = firstCell,
        });
        Assert.Same(firstRow.Item, variablesGrid.SelectedItem);

        var cellRightClick = new MouseButtonEventArgs(
            Mouse.PrimaryDevice,
            Environment.TickCount,
            MouseButton.Right)
        {
            RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent,
        };
        emphasizedName.RaiseEvent(cellRightClick);
        Assert.True(cellRightClick.Handled);
        var cellMenu = Assert.IsType<ContextMenu>(emphasizedName.ContextMenu);
        var cellMenuHeaders = cellMenu.Items.OfType<MenuItem>()
            .Select(item => item.Header?.ToString())
            .ToArray();
        Assert.Contains("Copy value", cellMenuHeaders);
        Assert.Contains("Copy selected cells", cellMenuHeaders);
        Assert.Same(firstRow.Item, variablesGrid.SelectedItem);
        cellMenu.IsOpen = false;
    }

    private static int CountRealizedRows(DataGrid grid, int itemCount) =>
        Enumerable.Range(0, itemCount)
            .Count(index => grid.ItemContainerGenerator.ContainerFromIndex(index) is DataGridRow);

    private static void AssertRealizedRowsAreBounded(int realizedRows, int itemCount, string phase)
    {
        Assert.True(realizedRows > 0, $"The Variables grid did not realize any rows for the {phase}.");
        Assert.True(realizedRows * 20 < itemCount,
            $"Row virtualization was ineffective for the {phase}: {realizedRows} of {itemCount} rows were realized.");
    }

    private static void AssertElementIsVisibleAndInside(
        FrameworkElement element,
        Window window,
        string elementName,
        string viewportName)
    {
        Assert.True(element.IsVisible, $"{elementName} is not visible at the {viewportName}.");
        Assert.True(element.ActualWidth > 0 && element.ActualHeight > 0,
            $"{elementName} was not measured at the {viewportName}.");
        var bounds = element.TransformToAncestor(window).TransformBounds(
            new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        const double layoutTolerance = 1;
        Assert.True(bounds.Left >= -layoutTolerance && bounds.Top >= -layoutTolerance
            && bounds.Right <= window.ActualWidth + layoutTolerance
            && bounds.Bottom <= window.ActualHeight + layoutTolerance,
            $"{elementName} is clipped at the {viewportName}: {bounds}.");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                yield return match;
            foreach (var descendant in Descendants<T>(child))
                yield return descendant;
        }
    }

    private static IEnumerable<T> LogicalDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject dependencyObject)
                continue;
            if (dependencyObject is T match)
                yield return match;
            foreach (var descendant in LogicalDescendants<T>(dependencyObject))
                yield return descendant;
        }
    }

    private static SettingsFileSnapshot CaptureFile(string path)
    {
        if (!File.Exists(path))
            return default;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new SettingsFileSnapshot(
            Exists: true,
            stream.Length,
            File.GetLastWriteTimeUtc(path),
            Convert.ToHexString(SHA256.HashData(stream)));
    }

    private sealed class BindingFailureTraceListener : TraceListener
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyList<string> Messages => _messages.ToArray();
        public override bool IsThreadSafe => true;

        public override void Write(string? message) => Record(message);
        public override void WriteLine(string? message) => Record(message);

        private void Record(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
                _messages.Enqueue(message.Trim());
        }
    }

    private readonly record struct SettingsFileSnapshot(
        bool Exists,
        long Length,
        DateTime LastWriteTimeUtc,
        string? Sha256);

    private sealed class AvailableUpdateService : IGitHubUpdateService
    {
        private static readonly SemanticVersion CurrentVersion = SemanticVersion.Parse("26.7.11");
        private static readonly SemanticVersion LatestVersion = SemanticVersion.Parse("26.7.12");
        private static readonly string InstallerName = $"PyMonitor-{LatestVersion}-win-x64.msi";
        private static readonly GitHubUpdateRelease Release = new(
            LatestVersion,
            $"v{LatestVersion}",
            $"PyMonitor v{LatestVersion}",
            new Uri($"https://github.com/example/PyMonitor/releases/tag/v{LatestVersion}"),
            DateTimeOffset.UtcNow,
            new GitHubReleaseAsset(
                InstallerName,
                new Uri($"https://github.com/example/PyMonitor/releases/download/v{LatestVersion}/{InstallerName}"),
                1),
            new GitHubReleaseAsset(
                InstallerName + ".sha256",
                new Uri($"https://github.com/example/PyMonitor/releases/download/v{LatestVersion}/{InstallerName}.sha256"),
                1));

        public Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new UpdateCheckResult(CurrentVersion, Release));

        public Task<VerifiedUpdateInstaller> DownloadAndVerifyInstallerAsync(
            GitHubUpdateRelease release,
            string destinationDirectory,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The smoke test must not start an update download.");
    }

    private sealed class NoOpUpdateInstallerLauncher : IUpdateInstallerLauncher
    {
        public void Launch(VerifiedUpdateInstaller installer) =>
            throw new InvalidOperationException("The smoke test must not start Windows Installer.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "PyMonitor.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
