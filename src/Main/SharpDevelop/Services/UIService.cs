// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System.Collections.Generic;
using System.IO;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Project;
using ICSharpCode.SharpDevelop.Templates;

namespace ICSharpCode.SharpDevelop
{
	/// <summary>
	/// Compatibility facade for the legacy menu commands. Project and item creation is implemented by
	/// the WPF Project Browser's dotnet-template workflow; its asynchronous UI cannot preserve the old
	/// synchronous result objects, which no live WPF caller consumes.
	/// </summary>
	class UIService : IUIService
	{
		public void ShowSolutionConfigurationEditorDialog(ISolution solution)
		{
			if (solution == null)
				return;
			Services.SolutionConfigurationWindow.Show(solution, System.Windows.Application.Current.MainWindow);
		}

		public FileTemplateResult ShowNewFileDialog(IProject project, DirectoryName directory, IEnumerable<TemplateCategory> templates)
		{
			var targetDirectory = directory?.ToString();
			if (string.IsNullOrWhiteSpace(targetDirectory)) {
				targetDirectory = SD.ProjectService.CurrentSolution?.Directory.ToString()
					?? Directory.GetCurrentDirectory();
			}

			ServiceSingleton.GetRequiredService<Services.IProjectBrowserController>()
				.AddNewItemInDirectory(targetDirectory);
			return null;
		}

		public ProjectTemplateResult ShowNewProjectDialog(ISolutionFolder solutionFolder, IEnumerable<TemplateCategory> templates)
		{
			if (solutionFolder is null) {
				ServiceSingleton.GetRequiredService<Services.IProjectBrowserController>().CreateNewSolution();
			} else {
				// All compiled callers that supplied a solution folder now use the WPF Project Browser
				// command directly. Do not silently pretend the legacy synchronous API completed.
				ServiceSingleton.GetRequiredService<IMessageService>().ShowMessage(
					"Use Solution Explorer > Add > New Project to add a project to an existing solution.",
					"New Project");
			}
			return null;
		}
	}
}
