using PyRuntimeInspector.App.Help;
using PyRuntimeInspector.App.ViewModels;
using Xunit;

namespace PyRuntimeInspector.App.Tests;

public sealed class HelpViewModelTests
{
    private static readonly string[] RequiredTopicIds =
    [
        "getting-started",
        "installation",
        "quick-attach",
        "cooperative-attach",
        "vscode-debugging",
        "managed-launch",
        "inspect-variables",
        "objects-classes",
        "arrays-images",
        "dataframes",
        "matplotlib",
        "memory",
        "execution-events",
        "gc-search",
        "sample-files",
        "troubleshooting",
        "shortcuts-safety",
    ];

    private static readonly string[] TopicsRequiringExamples =
    [
        "installation",
        "quick-attach",
        "cooperative-attach",
        "vscode-debugging",
        "managed-launch",
        "inspect-variables",
        "objects-classes",
        "arrays-images",
        "dataframes",
        "matplotlib",
        "memory",
        "sample-files",
        "troubleshooting",
    ];

    [Fact]
    public void CatalogCoversCoreWorkflowsWithActionableExamples()
    {
        var topicsById = HelpCatalog.Topics.ToDictionary(topic => topic.Id, StringComparer.Ordinal);

        Assert.All(RequiredTopicIds, id => Assert.Contains(id, topicsById.Keys));
        Assert.Equal(HelpCatalog.Topics.Count, topicsById.Count);
        Assert.All(HelpCatalog.Topics, topic =>
        {
            Assert.False(string.IsNullOrWhiteSpace(topic.Title), $"{topic.Id} must have a title.");
            Assert.False(string.IsNullOrWhiteSpace(topic.Summary), $"{topic.Id} must have a summary.");
            Assert.False(string.IsNullOrWhiteSpace(topic.Overview), $"{topic.Id} must have an overview.");
            Assert.NotEmpty(topic.Steps);
            Assert.All(topic.Steps, step =>
                Assert.False(string.IsNullOrWhiteSpace(step), $"{topic.Id} contains an empty step."));
            Assert.NotNull(topic.Notes);
        });

        Assert.All(
            TopicsRequiringExamples,
            id => Assert.False(string.IsNullOrWhiteSpace(topicsById[id].Example), $"{id} must have an example."));
    }

    [Theory]
    [InlineData("  DATAFRAME  ", "dataframes")]
    [InlineData("변수", "inspect-variables")]
    [InlineData("bootstrap", "quick-attach")]
    [InlineData("Start listener Copy environment", "cooperative-attach")]
    [InlineData("F1", "shortcuts-safety")]
    [InlineData("설치", "installation")]
    public void SearchFindsEnglishKoreanAndArticleBodyKeywords(string query, string expectedTopicId)
    {
        var matches = HelpCatalog.Search(query);

        var topic = Assert.Single(matches, candidate => candidate.Id == expectedTopicId);
        Assert.False(string.IsNullOrWhiteSpace(topic.Overview));
        Assert.NotEmpty(topic.Steps);
    }

    [Fact]
    public void ViewModelRepresentsEmptyQueryNoResultsAndClearSearchStates()
    {
        var viewModel = new HelpViewModel();

        Assert.Equal(HelpCatalog.Topics.Count, viewModel.FilteredTopics.Count);
        Assert.True(viewModel.HasResults);
        Assert.NotNull(viewModel.SelectedTopic);
        Assert.Contains(HelpCatalog.Topics.Count.ToString(), viewModel.ResultSummary, StringComparison.Ordinal);

        viewModel.SearchText = "   ";

        Assert.Equal(HelpCatalog.Topics.Count, viewModel.FilteredTopics.Count);
        Assert.True(viewModel.HasResults);
        Assert.NotNull(viewModel.SelectedTopic);

        viewModel.SearchText = "no-such-help-topic";

        Assert.Empty(viewModel.FilteredTopics);
        Assert.False(viewModel.HasResults);
        Assert.Null(viewModel.SelectedTopic);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.ResultSummary));
        Assert.Contains("0", viewModel.ResultSummary, StringComparison.Ordinal);

        viewModel.ClearSearch();

        Assert.Equal(string.Empty, viewModel.SearchText);
        Assert.Equal(HelpCatalog.Topics.Count, viewModel.FilteredTopics.Count);
        Assert.True(viewModel.HasResults);
        Assert.NotNull(viewModel.SelectedTopic);
        Assert.Contains(HelpCatalog.Topics.Count.ToString(), viewModel.ResultSummary, StringComparison.Ordinal);
    }
}
