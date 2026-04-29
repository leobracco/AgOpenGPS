using System;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using AgOpenGPS.AvaloniaApp.ViewModels;

namespace AgOpenGPS.AvaloniaApp
{
    public sealed class ViewLocator : IDataTemplate
    {
        public Control Build(object? data)
        {
            if (data is null) return new TextBlock { Text = "(null view-model)" };
            string fullName = data.GetType().FullName ?? "";
            string viewName = fullName.Replace(".ViewModels.", ".Views.").Replace("ViewModel", "View");
            var type = Type.GetType(viewName);
            if (type is null) return new TextBlock { Text = "View not found: " + viewName };
            return (Control)Activator.CreateInstance(type)!;
        }

        public bool Match(object? data) => data is ViewModelBase;
    }
}
