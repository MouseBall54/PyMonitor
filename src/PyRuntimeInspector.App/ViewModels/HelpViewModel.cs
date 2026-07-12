using PyRuntimeInspector.App.Help;
using PyRuntimeInspector.App.Infrastructure;

namespace PyRuntimeInspector.App.ViewModels;

public sealed class HelpViewModel : ObservableObject
{
    private string _searchText = string.Empty;
    private IReadOnlyList<HelpTopic> _filteredTopics;
    private HelpTopic? _selectedTopic;

    public HelpViewModel()
    {
        _filteredTopics = HelpCatalog.Topics;
        _selectedTopic = _filteredTopics.FirstOrDefault();
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
                RefreshResults();
        }
    }

    public IReadOnlyList<HelpTopic> FilteredTopics
    {
        get => _filteredTopics;
        private set => SetProperty(ref _filteredTopics, value);
    }

    public HelpTopic? SelectedTopic
    {
        get => _selectedTopic;
        set => SetProperty(ref _selectedTopic, value);
    }

    public bool HasResults => FilteredTopics.Count > 0;

    public string ResultSummary => string.IsNullOrWhiteSpace(SearchText)
        ? $"전체 도움말 {FilteredTopics.Count}개"
        : $"'{SearchText.Trim()}' 검색 결과 {FilteredTopics.Count}개";

    public void ClearSearch() => SearchText = string.Empty;

    private void RefreshResults()
    {
        var previousTopicId = SelectedTopic?.Id;
        var results = HelpCatalog.Search(SearchText);
        FilteredTopics = results;
        SelectedTopic = results.FirstOrDefault(topic => topic.Id == previousTopicId)
            ?? results.FirstOrDefault();
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ResultSummary));
    }
}
