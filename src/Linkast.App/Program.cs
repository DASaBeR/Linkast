// SPDX-License-Identifier: GPL-3.0-or-later
using Velopack;

namespace Linkast.App;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        // Must run first: handles install, uninstall and update hooks and may exit the process.
        // Does nothing when Linkast runs from a build folder.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
