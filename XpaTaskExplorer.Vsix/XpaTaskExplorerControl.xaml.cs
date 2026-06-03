using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace XpaTaskExplorer;

public partial class XpaTaskExplorerControl : UserControl
{
    public XpaTaskExplorerControl()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            RefreshTree();
        };
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        RefreshTree();
    }

    private void FilterTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (e.Key == Key.Enter)
            RefreshTree();
    }

    private void TaskTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (TaskTree.SelectedItem is XpaTaskNode node)
            OpenNode(node);
    }

    private void RefreshTree()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var projectDirectories = GetProjectDirectories();
            var nodes = XpaTaskTreeScanner.ScanProjects(projectDirectories, FilterTextBox.Text);
            TaskTree.ItemsSource = nodes;
            StatusTextBlock.Text = $"{CountNodes(nodes)} task(s), {projectDirectories.Count} projeto(s). Duplo clique abre o C#.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "Falha ao ler tasks: " + ex.Message;
        }
    }

    private static IReadOnlyList<string> GetProjectDirectories()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var dte = (DTE2?)Package.GetGlobalService(typeof(DTE));
        if (dte?.Solution is null || !dte.Solution.IsOpen)
            return Array.Empty<string>();

        var dirs = new List<string>();
        foreach (Project project in dte.Solution.Projects)
            CollectProjectDirectories(project, dirs);
        return dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void CollectProjectDirectories(Project project, ICollection<string> dirs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (project is null)
            return;

        try
        {
            if (!string.IsNullOrWhiteSpace(project.FullName))
            {
                var dir = Path.GetDirectoryName(project.FullName);
                if (!string.IsNullOrWhiteSpace(dir))
                    dirs.Add(dir);
            }
        }
        catch
        {
            // Some solution folders/unloaded projects throw when FullName is accessed.
        }

        try
        {
            if (project.ProjectItems is null)
                return;

            foreach (ProjectItem item in project.ProjectItems)
            {
                if (item.SubProject is not null)
                    CollectProjectDirectories(item.SubProject, dirs);
            }
        }
        catch
        {
            // Best-effort explorer: one problematic project must not block the tree.
        }
    }

    private static int CountNodes(IEnumerable<XpaTaskNode> nodes)
    {
        return nodes.Sum(node => 1 + CountNodes(node.Children));
    }

    private static void OpenNode(XpaTaskNode node)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var dte = (DTE2?)Package.GetGlobalService(typeof(DTE));
        if (dte is null || string.IsNullOrWhiteSpace(node.FilePath) || !File.Exists(node.FilePath))
            return;

        dte.ItemOperations.OpenFile(node.FilePath);
        if (dte.ActiveDocument?.Selection is TextSelection selection)
            selection.GotoLine(node.Line, true);
    }
}
