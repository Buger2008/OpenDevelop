#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.SharpDevelop.Services;

public sealed partial class SolutionConfigurationWindow : Window
{
    readonly ISolution _solution;
    bool _updating;

    sealed class ProjectMappingRow
    {
        readonly IProject _project;
        readonly Func<ConfigurationAndPlatform> _solutionConfiguration;
        string _configuration = string.Empty;
        string _platform = string.Empty;

        public ProjectMappingRow(IProject project, Func<ConfigurationAndPlatform> solutionConfiguration)
        {
            _project = project;
            _solutionConfiguration = solutionConfiguration;
            Name = project.Name;
            Configurations = project.ConfigurationNames.ToArray();
            Platforms = project.PlatformNames.ToArray();
            var mapping = project.ConfigurationMapping.GetProjectConfiguration(solutionConfiguration());
            _configuration = mapping.Configuration;
            _platform = mapping.Platform;
        }

        public string Name { get; }
        public IReadOnlyList<string> Configurations { get; }
        public IReadOnlyList<string> Platforms { get; }
        public string Configuration { get => _configuration; set { _configuration = value; Save(); } }
        public string Platform { get => _platform; set { _platform = value; Save(); } }
        void Save() => _project.ConfigurationMapping.SetProjectConfiguration(_solutionConfiguration(), new ConfigurationAndPlatform(_configuration, _platform));
    }

    SolutionConfigurationWindow(ISolution solution)
    {
        InitializeComponent();
        _solution = solution ?? throw new ArgumentNullException(nameof(solution));
        _updating = true;
        ConfigurationBox.ItemsSource = solution.ConfigurationNames.ToArray();
        PlatformBox.ItemsSource = solution.PlatformNames.ToArray();
        ConfigurationBox.SelectedItem = solution.ActiveConfiguration.Configuration;
        PlatformBox.SelectedItem = solution.ActiveConfiguration.Platform;
        _updating = false;
        RefreshMappings();
    }

    public static void Show(ISolution solution, Window owner)
    {
        var window = new SolutionConfigurationWindow(solution) { Owner = owner };
        window.ShowDialog();
    }

    ConfigurationAndPlatform SelectedSolutionConfiguration => new(
        ConfigurationBox.SelectedItem as string ?? _solution.ActiveConfiguration.Configuration,
        PlatformBox.SelectedItem as string ?? _solution.ActiveConfiguration.Platform);

    void OnSolutionSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_updating) RefreshMappings();
    }

    void RefreshMappings()
    {
        ProjectGrid.ItemsSource = new ObservableCollection<ProjectMappingRow>(
            _solution.Projects.Select(project => new ProjectMappingRow(project, () => SelectedSolutionConfiguration)));
    }

    void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
