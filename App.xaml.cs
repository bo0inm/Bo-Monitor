using System.Windows;
using System.Windows.Input;

namespace BoMonitor;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
	protected override void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);
		EventManager.RegisterClassHandler(
			typeof(Window),
			Keyboard.PreviewKeyDownEvent,
			new KeyEventHandler(OnPreviewKeyDown));
	}
	private void OnPreviewKeyDown(object sender, KeyEventArgs e)
	{
#if DEBUG
		if (e.Key == Key.Escape && Current.MainWindow is MainWindow mainWindow)
		{
			mainWindow.ExitApplication();
			e.Handled = true;
			return;
		}
#endif
	}
}

