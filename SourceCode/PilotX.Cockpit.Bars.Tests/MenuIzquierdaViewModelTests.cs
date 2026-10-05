using NUnit.Framework;
using System.Net.Http;
using PilotX.Cockpit.Bars.Services;
using PilotX.Cockpit.Bars.ViewModels;

namespace PilotX.Cockpit.Bars.Tests;

public class MenuIzquierdaViewModelTests
{
    [Test]
    public void ToggleSubmenu_OpensAndCloses()
    {
        var vm = new MenuIzquierdaViewModel(new GuidanceCommandClient(new HttpClient()));
        Assert.That(vm.OpenSubmenu, Is.Null);
        vm.ToggleSubmenuCommand.Execute("navegacion");
        Assert.That(vm.OpenSubmenu, Is.EqualTo("navegacion"));
        vm.ToggleSubmenuCommand.Execute("navegacion");   // segundo toque cierra
        Assert.That(vm.OpenSubmenu, Is.Null);
        vm.ToggleSubmenuCommand.Execute("config");        // otro abre y reemplaza
        Assert.That(vm.OpenSubmenu, Is.EqualTo("config"));
    }
}
