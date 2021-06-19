﻿using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Notifications;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;
using Indirect.Controls;
using Indirect.Entities.Wrappers;
using Indirect.Services;
using Indirect.Utilities;
using InstagramAPI;
using InstagramAPI.Classes.Core;
using InstagramAPI.Classes.User;
using InstagramAPI.Utils;
using Microsoft.Toolkit.Uwp.UI.Controls;
using Microsoft.Toolkit.Uwp.UI.Extensions;
using Microsoft.UI.Xaml.Controls;
using CoreWindowActivationState = Windows.UI.Core.CoreWindowActivationState;
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
        private ObservableCollection<BaseUser> NewMessageCandidates { get; } = new ObservableCollection<BaseUser>();


        public MainPage()
        {
            this.InitializeComponent();
            Window.Current.SetTitleBar(TitleBarElement);
            MainLayout.ViewStateChanged += OnViewStateChange;
            Window.Current.Activated += OnWindowFocusChange;
            Inbox = ViewModel.Inbox;
        }

        public async void ShowStatus(string title, string message,
            InfoBarSeverity severity = InfoBarSeverity.Informational)
        {
            await Dispatcher.QuickRunAsync(async () =>
            {
                if (MainStatusBar.IsOpen)
                {
                    MainStatusBar.IsOpen = false;
                    await Task.Delay(100);
                }

                MainStatusBar.Title = title;
                MainStatusBar.Message = message;
                MainStatusBar.Severity = severity;
                MainStatusBar.IsOpen = true;
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

            ViewModel.SyncClient.FailedToStart -= SyncClientOnFailedToStart;
            ViewModel.SyncClient.FailedToStart += SyncClientOnFailedToStart;

            UpdateSwitchAccountMenu();
        }

        private void SyncClientOnFailedToStart(object sender, Exception e)
        {
            DebugLogger.LogException(e);
            ShowStatus("Lost connection to the server",
                "New messages will not be updated. Please restart Indirect to reconnect.", InfoBarSeverity.Error);
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
            var confirmation = await confirmDialog.ShowAsync();
            if (confirmation != ContentDialogResult.Primary)
            {
                return;
            }

            await ((App) App.Current).CloseAllSecondaryViews();
            if (await ViewModel.Logout())
            {
                Frame.Navigate(typeof(LoginPage));
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

        private void OnViewStateChange(object sender, MasterDetailsViewState state)
        {
            BackButton.Visibility = state == MasterDetailsViewState.Details ? Visibility.Visible : Visibility.Collapsed;
            BackButtonPlaceholder.Visibility = BackButton.Visibility;
        }

        private async void MainLayout_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0 || e.AddedItems[0] == null)
            {
                return;
            }
            var inboxThread = (DirectThreadWrapper) e.AddedItems[0];
            if (!string.IsNullOrEmpty(inboxThread.ThreadId)) 
                ToastNotificationManager.History.RemoveGroup(inboxThread.ThreadId);

            var details = (TextBox) MainLayout.FindDescendantByName("MessageTextBox");
            details?.Focus(FocusState.Programmatic); // Focus to chat box after selecting a thread
            if (await Debouncer.Delay("OnThreadChanged", e.RemovedItems[0] == null ? 600 : 200).ConfigureAwait(false))
            {
                await inboxThread.MarkLatestItemSeen().ConfigureAwait(false);
            }
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

        #region NewMessage

        private void NewMessageSuggestBox_OnTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            if (string.IsNullOrEmpty(sender.Text) || sender.Text.Length > 50)
            {
                return;
            }

            ViewModel.SearchWithoutThreads(sender.Text,
                updatedList => NewMessageSuggestBox.ItemsSource = updatedList);
        }

        private void NewMessageSuggestBox_OnSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
        {
            var selectedItem = (BaseUser) args.SelectedItem;
            sender.Text = selectedItem.Username;
        }

        private void NewMessageSuggestBox_OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            if (args.ChosenSuggestion != null)
            {
                var selectedRecipient = (BaseUser) args.ChosenSuggestion;
                if (NewMessageCandidates.All(x => selectedRecipient.Username != x.Username))
                    NewMessageCandidates.Add(selectedRecipient);
            }
            else if (!string.IsNullOrEmpty(sender.Text))
            {
                ViewModel.SearchWithoutThreads(sender.Text, updatedList =>
                {
                    if (updatedList.Count == 0) return;
                    NewMessageCandidates.Add(updatedList[0]);
                });
            }
            sender.Text = string.Empty;
            sender.ItemsSource = null;
        }

        private void NewMessageClearAll_OnClick(object sender, RoutedEventArgs e)
        {
            NewMessageCandidates.Clear();
        }

        private async void ChatButton_OnClick(object sender, RoutedEventArgs e)
        {
            NewThreadFlyout.Hide();
            if (NewMessageCandidates.Count == 0 || NewMessageCandidates.Count > 32) return;
            var userIds = NewMessageCandidates.Select(x => x.Pk);
            await ViewModel.CreateAndOpenThread(userIds);
            NewMessageCandidates.Clear();
        }

        private void ClearSingleCandidateButton_OnClick(object sender, RoutedEventArgs e)
        {
            var target = (BaseUser) (sender as FrameworkElement)?.DataContext;
            if (target == null) return;
            NewMessageCandidates.Remove(target);
        }

        private void Candidate_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            (sender as FrameworkElement).FindDescendantByName("ClearSingleCandidateButton").Visibility = Visibility.Visible;
        }

        private void Candidate_OnPointerExited(object sender, PointerRoutedEventArgs e)
        {
            (sender as FrameworkElement).FindDescendantByName("ClearSingleCandidateButton").Visibility = Visibility.Collapsed;
        }

        private void ClearSingleCandidateSwipe_OnInvoked(SwipeItem sender, SwipeItemInvokedEventArgs args)
        {
            var target = (BaseUser) args.SwipeControl.DataContext;
            if (target == null) return;
            NewMessageCandidates.Remove(target);
        }

        private void NewMessageSuggestBox_OnProcessKeyboardAccelerators(UIElement sender, ProcessKeyboardAcceleratorEventArgs args)
        {
            if (args.Key == VirtualKey.Escape && args.Modifiers == VirtualKeyModifiers.None)
            {
                args.Handled = true;
                NewThreadFlyout.Hide();
            }
                
        }

        #endregion

        private void TogglePendingInbox_OnClick(object sender, RoutedEventArgs e)
        {
            Inbox = Inbox == ViewModel.Inbox ? ViewModel.PendingInbox : ViewModel.Inbox;
        }

        private async void ReelsFeed_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var reelsFeed = (ListView) sender;
            int selected;
            lock (sender)
            {
                if (reelsFeed.SelectedIndex == -1) return;
                selected = reelsFeed.SelectedIndex;
                reelsFeed.SelectedIndex = -1;
            }
            var reelsWrapper = await ViewModel.ReelsFeed.PrepareFlatReelsContainer(selected);
            this.Frame.Navigate(typeof(ReelPage), reelsWrapper);
        }

        private async void StoriesSectionTitle_OnTapped(object sender, TappedRoutedEventArgs e)
        {
            await ViewModel.ReelsFeed.UpdateReelsFeedAsync(ReelsTrayFetchReason.PullToRefresh);
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            var about = new AboutDialog();
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
                await ((App)App.Current).CloseAllSecondaryViews();
                await ViewModel.SaveDataAsync();
                await ViewModel.SwitchAccountAsync(session);
                Frame.Navigate(typeof(MainPage));
            }
        }
    }
}
