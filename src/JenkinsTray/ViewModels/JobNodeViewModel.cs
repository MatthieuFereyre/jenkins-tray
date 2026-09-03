using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using JenkinsTray.Models;
using JenkinsTray.Services;

namespace JenkinsTray.ViewModels;

/// <summary>
/// A folder or a job in the explorer tree. Both carry a checkbox: a job holds its own state, a
/// folder mirrors the jobs below it and applies any change to all of them.
/// </summary>
public partial class JobNodeViewModel : ObservableObject
{
    private readonly JobsViewModel _owner;
    private bool _silent;

    public JobNodeViewModel(JobNode node, JobsViewModel owner, ISet<string> monitored, JobNodeViewModel? parent = null)
    {
        _owner = owner;
        Parent = parent;
        _silent = true;

        Name = node.Name;
        FullName = node.FullName;
        Path = node.Path;
        IsFolder = node.IsFolder;

        var (status, building) = StatusMap.FromColor(node.Color);
        Status = status;
        StatusTooltip = building ? $"{StatusMap.Label(status)} (build en cours)" : StatusMap.Label(status);

        foreach (var child in node.Children.OrderByDescending(c => c.IsFolder).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            Children.Add(new JobNodeViewModel(child, owner, monitored, this));

        IsChecked = IsFolder ? StateFromChildren() : monitored.Contains(node.FullName);
        _silent = false;
    }

    public JobNodeViewModel? Parent { get; }
    public string Name { get; }
    public string FullName { get; }
    public string Path { get; }
    public bool IsFolder { get; }
    public bool IsJob => !IsFolder;
    public BuildStatus Status { get; }
    public string StatusTooltip { get; }

    public string CheckBoxTooltip => IsFolder
        ? "Surveiller tous les jobs de ce dossier"
        : "Surveiller ce job";

    public ObservableCollection<JobNodeViewModel> Children { get; } = [];

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isVisible = true;

    /// <summary>Null on a folder whose jobs are only partly monitored.</summary>
    [ObservableProperty] private bool? _isChecked;

    partial void OnIsCheckedChanged(bool? value)
    {
        if (_silent)
            return;

        // Clicking a folder — indeterminate included — pushes its new state onto everything below.
        var state = value ?? false;

        foreach (var child in VisibleChildren)
            child.ApplyStateSilently(state);

        Parent?.RefreshFromChildren();
        _owner.OnNodeCheckedChanged(this, state);
    }

    /// <summary>
    /// The jobs this node stands for right now. A folder never reaches past the filter: what the
    /// tree hides is neither counted in its checkbox nor touched by a click on it.
    /// </summary>
    public IEnumerable<JobNodeViewModel> VisibleJobs()
    {
        if (IsJob)
        {
            yield return this;
            yield break;
        }

        foreach (var child in VisibleChildren)
            foreach (var job in child.VisibleJobs())
                yield return job;
    }

    public IEnumerable<JobNodeViewModel> Descend()
    {
        yield return this;

        foreach (var child in Children)
            foreach (var descendant in child.Descend())
                yield return descendant;
    }

    /// <summary>
    /// Applies the search filter bottom-up: a node stays visible when it matches or when any
    /// descendant does. Matching inside a folder auto-expands the path to it.
    /// </summary>
    public bool ApplyFilter(string? query)
    {
        var selfMatches = string.IsNullOrWhiteSpace(query)
            || Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || FullName.Contains(query, StringComparison.CurrentCultureIgnoreCase);

        var anyChildMatches = false;
        foreach (var child in Children)
            anyChildMatches |= child.ApplyFilter(query);

        IsVisible = selfMatches || anyChildMatches;

        if (!string.IsNullOrWhiteSpace(query) && anyChildMatches)
            IsExpanded = true;

        return IsVisible;
    }

    public void SetExpanded(bool expanded)
    {
        if (IsFolder)
            IsExpanded = expanded;

        foreach (var child in Children)
            child.SetExpanded(expanded);
    }

    /// <summary>Sets the checkbox without notifying the owner — used while (re)loading the tree.</summary>
    public void SetCheckedSilently(bool? value)
    {
        if (IsChecked == value)
            return;

        _silent = true;
        IsChecked = value;
        _silent = false;
    }

    /// <summary>Rebuilds the folder states bottom-up, once the jobs have been set from the settings.</summary>
    public void RefreshFolderStates()
    {
        foreach (var child in Children)
            child.RefreshFolderStates();

        if (IsFolder)
            SetCheckedSilently(StateFromChildren());
    }

    private void ApplyStateSilently(bool state)
    {
        SetCheckedSilently(state);

        foreach (var child in VisibleChildren)
            child.ApplyStateSilently(state);
    }

    /// <summary>Walks back up so every folder above reflects the change that just happened.</summary>
    private void RefreshFromChildren()
    {
        if (IsFolder)
            SetCheckedSilently(StateFromChildren());

        Parent?.RefreshFromChildren();
    }

    /// <summary>All, none, or a mix — the three states a folder checkbox can show.</summary>
    private bool? StateFromChildren()
    {
        var children = VisibleChildren.ToList();
        if (children.Count == 0)
            return false;

        var first = children[0].IsChecked;
        return children.All(child => child.IsChecked == first) ? first : null;
    }

    private IEnumerable<JobNodeViewModel> VisibleChildren => Children.Where(child => child.IsVisible);
}
