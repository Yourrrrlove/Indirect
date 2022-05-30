﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Notifications;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;
using Indirect.Entities;
using Indirect.Entities.Wrappers;
using Indirect.Services;
using Indirect.Utilities;
using InstagramAPI;
using InstagramAPI.Classes.Core;
using InstagramAPI.Classes.User;
using InstagramAPI.Utils;
using Microsoft.Toolkit.Uwp.UI;
using Microsoft.Toolkit.Uwp.UI.Controls;
using Microsoft.UI.Xaml.Controls;
using SwipeItem = Windows.UI.Xaml.Controls.SwipeItem;
using SwipeItemInvokedEventArgs = Windows.UI.Xaml.Controls.SwipeItemInvokedEventArgs;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace Indirect.Pages
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        public static readonly DependencyProperty InboxProperty = DependencyProperty.Register(
            nameof(Inbox),
            typeof(InboxWrapper),
            typeof(MainPage),
            new PropertyMetadata(null));

        internal InboxWrapper Inbox
        {
            get => (InboxWrapper) GetValue(InboxProperty);
            set => SetValue(InboxProperty, value);
        }

        private MainViewModel ViewModel => ((App) Application.Current).ViewModel;
        private CoreViewHandle _reelPageView;
        private bool _loggedOut;

        public MainPage()
        {
            this.InitializeComponent();
            Window.Current.SetTitleBar(TitleBarElement);
            MainLayout.ViewStateChanged += OnViewStateChange;
            MainLayout.SelectionChanged += MainLayout_OnSelectionChanged;
            MainLayout.ItemClick += MainLayout_OnItemClick;
            Window.Current.Activated += OnWindowFocusChange;
            SystemNavigationManager.GetForCurrentView().BackRequested += SystemNavigationManager_BackRequested;
            AdaptiveLayoutStateGroup.CurrentStateChanged += AdaptiveLayoutStateGroupOnCurrentStateChanged;
            Inbox = ViewModel.Inbox;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            MainLayout.ViewStateChanged -= OnViewStateChange;
            MainLayout.SelectionChanged -= MainLayout_OnSelectionChanged;
            MainLayout.ItemClick -= MainLayout_OnItemClick;
            Window.Current.Activated -= OnWindowFocusChange;
            SystemNavigationManager.GetForCurrentView().BackRequested -= SystemNavigationManager_BackRequested;
            AdaptiveLayoutStateGroup.CurrentStateChanged -= AdaptiveLayoutStateGroupOnCurrentStateChanged;
            ViewModel.InstaApi.HttpClient.LoginRequired -= ClientOnLoginRequired;
            base.OnNavigatedFrom(e);
        }

        public async void ShowStatus(string title, string message,
            InfoBarSeverity severity = InfoBarSeverity.Informational, int timeout = 0)
        {
            await Dispatcher.QuickRunAsync(async () =>
            {
                if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(message))
                {
                    Debouncer.CancelDelay(nameof(MainStatusBar));
                    MainStatusBar.IsOpen = false;
                    return;
                }

                if (MainStatusBar.IsOpen)
                {
                    MainStatusBar.IsOpen = false;
                    await Task.Delay(100);
                }

                MainStatusBar.Title = title;
                MainStatusBar.Message = message;
                MainStatusBar.Severity = severity;
                MainStatusBar.IsOpen = true;

                if (timeout > 0)
                {
                    if (await Debouncer.Delay(nameof(MainStatusBar), TimeSpan.FromSeconds(timeout)))
                    {
                        MainStatusBar.IsOpen = false;
                    }
                }
                else
                {
                    Debouncer.CancelDelay(nameof(MainStatusBar));
                }
            }, CoreDispatcherPriority.Low);
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Frame.BackStack.Clear();
            if (e?.NavigationMode != NavigationMode.Back)
            {
                await ViewModel.OnLoggedIn();
            }

            ViewModel.InstaApi.HttpClient.LoginRequired -= ClientOnLoginRequired;
            ViewModel.InstaApi.HttpClient.LoginRequired += ClientOnLoginRequired;

            UpdateSwitchAccountMenu();
        }

        private async void ClientOnLoginRequired(object sender, EventArgs e)
        {
            lock (this)
            {
                if (_loggedOut)
                {
                    return;
                }

                _loggedOut = true;
            }

            await Dispatcher.QuickRunAsync(async () =>
            {
                try
                {
                    var dialog = new ContentDialog
                    {
                        Title = "You've been logged out",
                        Content = "Please log back in.",
                        CloseButtonText = "Close",
                        DefaultButton = ContentDialogButton.Close
                    };
                    await dialog.ShowAsync();

                    await Logout();
                }
                catch (Exception)
                {
                    // pass
                }
            });
        }

        private void SystemNavigationManager_BackRequested(object sender, BackRequestedEventArgs e)
        {
            if (ImmersiveControl.IsOpen)
            {
                e.Handled = true;
                ImmersiveControl.Close();
            }
        }

        private Visibility GetReelsTrayVisibility(int reelsCount)
        {
            if (AdaptiveLayoutStateGroup.CurrentState == Intermediate)
            {
                return Visibility.Collapsed;
            }

            return reelsCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AdaptiveLayoutStateGroupOnCurrentStateChanged(object sender, VisualStateChangedEventArgs e)
        {
            ReelsTray.Visibility = GetReelsTrayVisibility(ViewModel.ReelsFeed.Reels.Count);
        }

        private async void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            var confirmDialog = new ContentDialog()
            {
                Title = "Log out of Indirect?",
                Content = "Logging out will delete all session data of this profile.",
                CloseButtonText = "Close",
                PrimaryButtonText = "Log Out",
                DefaultButton = ContentDialogButton.Primary
            };

            try
            {
                var confirmation = await confirmDialog.ShowAsync();
                if (confirmation != ContentDialogResult.Primary)
                {
                    return;
                }
            }
            catch (Exception)
            {
                return;
            }

            await Logout();
        }

        private async Task Logout()
        {
            await ((App)App.Current).CloseAllSecondaryViews();
            if (await ViewModel.Logout())
            {
                Frame.Navigate(typeof(LoginPage));
                Frame.BackStack.Clear();
            }
            else
            {
                Frame.Navigate(typeof(MainPage));
            }
        }
        
        private void DetailsBackButton_OnClick(object sender, RoutedEventArgs e) => ViewModel.SetSelectedThreadNull();

        private void OnWindowFocusChange(object sender, WindowActivatedEventArgs e)
        {
            if (e.WindowActivationState == CoreWindowActivationState.Deactivated)
            {
                BackButton.IsEnabled = false;
                AppTitleTextBlock.Opacity = 0.5;
            }
            else
            {

                BackButton.IsEnabled = true;
                AppTitleTextBlock.Opacity = 1;
            }
        }

        private void OnViewStateChange(object sender, ListDetailsViewState state)
        {
            BackButton.Visibility = state == ListDetailsViewState.Details ? Visibility.Visible : Visibility.Collapsed;
            BackButtonPlaceholder.Visibility = BackButton.Visibility;
        }

        private async void MainLayout_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems == null || e.AddedItems.Count == 0 || e.AddedItems[0] == null)
            {
                return;
            }

            var inboxThread = (DirectThreadWrapper) e.AddedItems[0];
            this.Log("Thread change invoked: " + inboxThread.Users[0].Username);
            try
            {
                if (!string.IsNullOrEmpty(inboxThread.ThreadId))
                {
                    ToastNotificationManager.History.RemoveGroup(inboxThread.ThreadId);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.LogException(ex);
            }

            if (await Debouncer.Delay("OnThreadChanged", e.RemovedItems[0] == null ? 600 : 200)
                .ConfigureAwait(false))
            {
                await inboxThread.MarkLatestItemSeen().ConfigureAwait(false);
            }
        }

        private async void MainLayout_OnItemClick(object sender, ItemClickEventArgs e)
        {
            var details = (TextBox)MainLayout.FindDescendant("MessageTextBox");
            if (details == null) return;
            if (MainLayout.SelectedIndex == -1 && MainLayout.ViewState == ListDetailsViewState.Both)
            {
                await Task.Delay(100);
            }

            details.Focus(FocusState.Programmatic); // Focus to chat box after selecting a thread
            this.Log("Focus message box");
        }

        #region Search
        private void SearchBox_OnTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            if (string.IsNullOrEmpty(sender.Text) || sender.Text.Length > 50)
            {
                return;
            }

            ViewModel.Search(sender.Text,
                updatedList => SearchBox.ItemsSource = updatedList);
        }

        private void SearchBox_OnSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
        {
            var selectedItem = (DirectThreadWrapper) args.SelectedItem;
            sender.Text = selectedItem.Source.Title;
        }

        private void SearchBox_OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            if (args.ChosenSuggestion != null)
            {
                ViewModel.MakeProperInboxThread((DirectThreadWrapper) args.ChosenSuggestion);
            }
            else if (!string.IsNullOrEmpty(sender.Text))
            {
                ViewModel.Search(sender.Text, updatedList =>
                {
                    if (updatedList.Count == 0) return;
                    ViewModel.MakeProperInboxThread(updatedList[0]);
                });
            }

            sender.Text = string.Empty;
            sender.ItemsSource = null;
        }

        #endregion

        private void TogglePendingInbox_OnClick(object sender, RoutedEventArgs e)
        {
            Inbox = Inbox == ViewModel.Inbox ? ViewModel.PendingInbox : ViewModel.Inbox;
        }

        private async void StoriesSectionTitle_OnTapped(object sender, TappedRoutedEventArgs e)
        {
            await ViewModel.ReelsFeed.UpdateReelsFeedAsync(ReelsTrayFetchReason.PullToRefresh);
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            var about = new ContentDialog
            {
                Title = "About",
                CloseButtonText = "Close",
                Content = new AboutPage()
            };
            _ = about.ShowAsync();
        }

        private void ThemeItem_Click(object sender, RoutedEventArgs e)
        {
            var item = (MenuFlyoutItem)sender;
            switch (item.Text)
            {
                case "System":
                    SettingsService.SetGlobal("Theme", "System");
                    break;

                case "Dark":
                    SettingsService.SetGlobal("Theme", "Dark");
                    break;

                case "Light":
                    SettingsService.SetGlobal("Theme", "Light");
                    break;
            }

            var dialog = new ContentDialog
            {
                Title = "Saved",
                Content = "Please relaunch the app to see the result.",
                CloseButtonText = "Done",
                DefaultButton = ContentDialogButton.Close
            };

            _ = dialog.ShowAsync();
        }

        private async void Profile_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(ViewModel?.LoggedInUser?.Username)) return;
            var username = ViewModel.LoggedInUser.Username;
            var uri = new Uri("https://www.instagram.com/" + username);
            await Windows.System.Launcher.LaunchUriAsync(uri);
        }

        private async void TestButton_OnClick(object sender, RoutedEventArgs e)
        {
            //await ContactsService.DeleteAllAppContacts();
            await Task.Delay(2000).ConfigureAwait(false);
            ShowStatus("Lost connection to the server",
                "New messages will not be updated. Please restart Indirect to reconnect.", InfoBarSeverity.Error);
        }

        private async void MasterMenuButton_OnImageExFailed(object sender, ImageExFailedEventArgs e)
        {
            await ViewModel.UpdateLoggedInUser();
        }

        private async void AddAccountButton_OnClick(object sender, RoutedEventArgs e)
        {
            await ((App)App.Current).CloseAllSecondaryViews();
            Frame.Navigate(typeof(LoginPage));
            await ViewModel.SaveDataAsync().ConfigureAwait(false);
        }

        private void UpdateSwitchAccountMenu()
        {
            var menuItems = SwitchAccountMenu.Items;
            while (menuItems?.Count > 1)
            {
                menuItems.RemoveAt(0);
            }

            if (ViewModel.AvailableSessions.Length == 0 || menuItems == null)
            {
                return;
            }

            menuItems.Insert(0, new MenuFlyoutSeparator());
            foreach (var sessionContainer in ViewModel.AvailableSessions)
            {
                var item = new MenuFlyoutItem
                {
                    Icon = new BitmapIcon {UriSource = sessionContainer.ProfilePicture, ShowAsMonochrome = false},
                    Text = sessionContainer.Session.LoggedInUser.Username,
                    DataContext = sessionContainer.Session
                };
                item.Click += SwitchAccountItem_OnClick;
                menuItems.Insert(0, item);
            }
        }

        private async void SwitchAccountItem_OnClick(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is UserSessionData session)
            {
                ViewModel.InstaApi.HttpClient.LoginRequired -= ClientOnLoginRequired;
                await ((App)App.Current).CloseAllSecondaryViews();
                await ViewModel.SaveDataAsync();
                await ViewModel.SwitchAccountAsync(session);
                Frame.Navigate(typeof(MainPage));
            }
        }

        private async void ReelsFeed_OnItemClicked(object sender, ItemClickEventArgs e)
        {
            var reelsWrapper = (ReelWrapper)e.ClickedItem;
            if (reelsWrapper != null)
            {
                if (_reelPageView != null && !ViewModel.ShowStoryInNewWindow)
                {
                    await CloseSecondaryReelViews();
                }

                if (DeviceFamilyHelpers.MultipleViewsSupport && ViewModel.ShowStoryInNewWindow)
                {
                    OpenReelInNewWindow(reelsWrapper);
                }
                else
                {
                    var flatReels = await ViewModel.ReelsFeed.PrepareFlatReelsContainer(reelsWrapper);
                    this.Frame.Navigate(typeof(ReelPage), flatReels);
                }
            }
        }

        private void SearchBox_OnKeyboardAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            args.Handled = true;
            SearchBox.Focus(FocusState.Programmatic);
        }

        private async Task CloseSecondaryReelViews()
        {
            CoreViewHandle handle = _reelPageView;
            if (handle != null)
            {
                await App.CloseSecondaryView(handle.Id);
                _reelPageView = null;
            }
        }

        private void OpenReelInNewWindow(ReelWrapper reelWrapper)
        {
            int selectedIndex = ViewModel.ReelsFeed.LatestReelsFeed.IndexOf(reelWrapper.Source);

            if (_reelPageView != null && ((App)App.Current).IsViewOpen(_reelPageView.Id))
            {
                async void PrepareReels()
                {
                    FlatReelsContainer flatReels = await PrepareReelsForSecondaryView(selectedIndex);
                    Frame frame = (Frame)Window.Current.Content;
                    frame.Navigate(typeof(ReelPage), flatReels);
                    frame.BackStack.Clear();
                }

                _reelPageView.CoreView.DispatcherQueue.TryEnqueue(PrepareReels);
            }
            else
            {
                CoreApplicationView view = CoreApplication.CreateNewView();

                async void RunOnMainThread()
                {
                    FlatReelsContainer flatReels = await PrepareReelsForSecondaryView(selectedIndex);
                    int viewId = await ((App)App.Current).CreateAndShowNewView(typeof(ReelPage), flatReels, view);
                    _reelPageView = new CoreViewHandle(viewId, view);
                }

                view.DispatcherQueue.TryEnqueue(RunOnMainThread);
            }

        }

        private async Task<FlatReelsContainer> PrepareReelsForSecondaryView(int selectedIndex)
        {
            List<ReelWrapper> wrappedReels = ViewModel.ReelsFeed.LatestReelsFeed.Select(x => new ReelWrapper(x)).ToList();
            FlatReelsContainer flatReels = await ViewModel.ReelsFeed.PrepareFlatReelsContainer(wrappedReels, selectedIndex);
            flatReels.SecondaryView = true;
            return flatReels;
        }

        private void ReelItemMenuFlyout_OnOpening(object sender, object e)
        {
            var menu = (MenuFlyout) sender;
            var dataContext = menu.Target?.DataContext ?? (menu.Target as ContentControl)?.Content;
            foreach (var item in menu.Items)
            {
                item.DataContext = dataContext;
            }
        }
    }
}
