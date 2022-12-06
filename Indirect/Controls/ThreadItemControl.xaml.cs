﻿using System;
using System.Collections.Generic;
using System.Linq;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Input;
using Indirect.Entities.Wrappers;
using Indirect.Utilities;
using InstagramAPI.Classes.Direct;
using InstagramAPI.Classes.Media;
using Microsoft.Toolkit.Uwp.UI;
using NeoSmart.Unicode;
using System.Numerics;
using Windows.UI.Xaml.Hosting;
using Indirect.Converters;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace Indirect.Controls
{
    internal sealed partial class ThreadItemControl : UserControl
    {
        private static MainViewModel ViewModel => ((App)Application.Current).ViewModel;
        private bool _visible;

        public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
            nameof(Item),
            typeof(DirectItemWrapper),
            typeof(ThreadItemControl),
            new PropertyMetadata(null, OnItemSourceChanged));

        public DirectItemWrapper Item
        {
            get => (DirectItemWrapper)GetValue(ItemProperty);
            set => SetValue(ItemProperty, value);
        }

        private static void OnItemSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var view = (ThreadItemControl)d;
            var item = (DirectItemWrapper)e.NewValue;
            view.ProcessItem();
            view.UpdateItemMargin();
            view.UpdateContextMenu();
            view.Bindings.Update();
        }

        public ThreadItemControl()
        {
            this.InitializeComponent();
            Unloaded += OnUnloaded;
            MainContentControl.SizeChanged += MainContentControl_SizeChanged;
            MainContentControl.EffectiveViewportChanged += MainContentControl_EffectiveViewportChanged;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            Unloaded -= OnUnloaded;
            MainContentControl.SizeChanged -= MainContentControl_SizeChanged;
            MainContentControl.EffectiveViewportChanged -= MainContentControl_EffectiveViewportChanged;
        }

        private void MainContentControl_EffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
        {
            _visible = args.BringIntoViewDistanceY - sender.ActualHeight <= 0;
        }

        private void MainContentControl_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (Item == null || !_visible) return;

            var itemType = Item.Source.ItemType;
            if (itemType == DirectItemType.ActionLog || itemType == DirectItemType.Like) return;

            if (e.PreviousSize == e.NewSize) return;

            var prev = e.PreviousSize.ToVector2();
            var next = e.NewSize.ToVector2();

            var anim = Window.Current.Compositor.CreateVector3KeyFrameAnimation();
            anim.InsertKeyFrame(0, new Vector3(prev / next, 1));
            anim.InsertKeyFrame(1, Vector3.One);

            var content = ((ContentControl)sender).ContentTemplateRoot;
            var panel = ElementCompositionPreview.GetElementVisual(content);
            panel.CenterPoint = new Vector3(Item.FromMe ? next.X : 0, 0, 0);
            panel.StartAnimation("Scale", anim);

            var textBlock = content.FindDescendant<TextBlock>();
            if (textBlock != null)
            {
                var factor = Window.Current.Compositor.CreateExpressionAnimation("Vector3(1 / content.Scale.X, 1 / content.Scale.Y, 1)");
                factor.SetReferenceParameter("content", panel);

                var text = ElementCompositionPreview.GetElementVisual(content.FindDescendant<TextBlock>());
                text.StartAnimation("Scale", factor);
            }
        }

        public void OnItemClick()
        {
            if (Item.Source.ItemType != DirectItemType.AnimatedMedia && Item.FullImageUri != null ||
                Item.VideoUri != null)
            {
                OpenItemInImmersiveControl();
            }
            else
            {
                OpenWebLink(this, null);
            }
        }

        private void ProcessItem()
        {
            Item.Source.Timestamp = Item.Source.Timestamp.ToLocalTime();
            if (Item.Source.ItemType == DirectItemType.Link)
                Item.Source.Text = Item.Source.Link.Text;
        }

        private void UpdateContextMenu()
        {
            switch (Item.Source.ItemType)
            {
                case DirectItemType.ActionLog:
                    ItemContainer.Visibility = Item.Source.HideInThread ? Visibility.Collapsed : Visibility.Visible;
                    ContextFlyout = null;
                    break;

                case DirectItemType.VideoCallEvent:
                    ContextFlyout = null;
                    break;

                case DirectItemType.Text:
                    MenuCopyOption.Visibility = Visibility.Visible;
                    break;

                default:
                    break;
            }

            if (Item.VideoUri != null || Item.FullImageUri != null)
            {
                DownloadMenuItem.Visibility = Visibility.Visible;
            }
        }

        private Thickness GetRelativeMargin(RelativeItemMode mode)
        {
            switch (mode)
            {
                case RelativeItemMode.None:
                    return new Thickness(0, 4, 0, 4);
                case RelativeItemMode.Before:
                    return new Thickness(0, 1, 0, 4);
                case RelativeItemMode.After:
                    return new Thickness(0, 4, 0, 1);
                case RelativeItemMode.Both:
                    return new Thickness(0, 1, 0, 1);
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unexpected RelativeItemMode");
            }
        }

        private void UpdateItemMargin()
        {
            if (Item == null || Item.Source.ItemType == DirectItemType.ActionLog) return;
            MainContentControl.Margin = Item.FromMe ? new Thickness(50, 0, 0, 0) : new Thickness(0, 0, 50, 0);
        }

        private HorizontalAlignment GetSeenIndicatorAlignment(bool fromMe) => fromMe ? HorizontalAlignment.Right : HorizontalAlignment.Left;

        private string GetSeenText(Dictionary<long, LastSeen> lastSeenAt)
        {
            if (lastSeenAt == null || lastSeenAt.Count == 0)
            {
                return string.Empty;
            }

            try
            {
                var viewerId = Item.Parent.Source.ViewerId;
                if (!lastSeenAt.TryGetValue(viewerId, out var viewerLastSeen) || viewerLastSeen.Timestamp > Item.Source.Timestamp)
                    return string.Empty;

                // We can no longer check last seen using item ID matching. Using timestamp instead.
                var seenList = lastSeenAt.Where(x =>
                        x.Value != null &&
                        x.Value.Timestamp >= Item.Source.Timestamp &&    // Seen timestamp is newer than item timestamp
                        x.Key != viewerId &&                // Not from viewer
                        x.Key != Item.Sender.Pk             // Not from sender
                    ).ToArray();
                if (seenList.Length == 0)
                {
                    return string.Empty;
                }

                if (Item.Parent.Users.Count == 1)
                {
                    return Item.FromMe && Item.Parent.LastPermanentItem?.Source.ItemId != Item.Source.ItemId
                        ? string.Empty 
                        : $"Seen {RelativeTimeConverter.Convert(seenList[0].Value.Timestamp)}";
                }

                if (Item.Parent.Users.Count <= seenList.Length)
                {
                    return "Seen by everyone";
                }

                var seenUsers = seenList.Select(x => Item.Parent.Users.FirstOrDefault(y => x.Key == y.Pk)?.Username).ToArray();
                if (seenUsers.Length <= 3)
                {
                    return "Seen by " + string.Join(", ", seenUsers);
                }

                return $"Seen by {seenUsers[0]}, {seenUsers[1]}, {seenUsers[2]} and {seenUsers.Length - 3} others";
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private void OpenItemInImmersiveControl()
        {
            var frame = Window.Current.Content as Frame;
            var page = frame?.Content as Page;
            var immersiveControl = page?.FindChild<ImmersiveControl>();
            immersiveControl?.Open(Item);
        }

        private void ImageFrame_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (Item.Source.ItemType == DirectItemType.AnimatedMedia) return;
            var uri = Item.FullImageUri;
            if (uri == null) return;
            OpenItemInImmersiveControl();
        }

        private void VideoPopupButton_OnTapped(object sender, TappedRoutedEventArgs e)
        {
            var uri = Item.VideoUri;
            if (uri == null) return;
            OpenItemInImmersiveControl();
        }

        private void OpenMediaButton_OnClick(object sender, RoutedEventArgs e)
        {
            ImageFrame_Tapped(sender, new TappedRoutedEventArgs());
        }

        private void OpenWebLink(object sender, TappedRoutedEventArgs e)
        {
            if (Item.NavigateUri == null) return;
            _ = Windows.System.Launcher.LaunchUriAsync(Item.NavigateUri);
        }

        private void ReelShareImage_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (Item.Source.ReelShareMedia?.Media.MediaType == InstaMediaType.Image ||
                Item.Source.StoryShareMedia?.Media.MediaType == InstaMediaType.Image)
            {
                ImageFrame_Tapped(sender, e);
            }
            else
            {
                VideoPopupButton_OnTapped(sender, e);
            }
        }

        private async void Item_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (Item.ObservableReactions.MeLiked || Item.Source.ItemType == DirectItemType.VideoCallEvent) return;
            await ViewModel.ChatService.ReactToItem(Item, Emoji.RedHeart.ToString());
        }

        private void MenuCopyOption_Click(object sender, RoutedEventArgs e)
        {
            var border = MainContentControl.ContentTemplateRoot as Border;
            var textBlock = border?.Child as TextBlock;
            if (textBlock == null) return;
            var dataPackage = new DataPackage();
            dataPackage.RequestedOperation = DataPackageOperation.Copy;
            dataPackage.SetText(textBlock.Text);
            Clipboard.SetContent(dataPackage);
        }

        private void ConfigTooltip_OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            var tooltip = new ToolTip();
            tooltip.Content = $"{Item.Source.Timestamp:f}";
            tooltip.PlacementRect = new Rect(0, 12, e.NewSize.Width, e.NewSize.Height);
            ToolTipService.SetToolTip((DependencyObject)sender, tooltip);
        }

        private async void UnsendMessage(object sender, RoutedEventArgs e)
        {
            var confirmDialog = new ContentDialog
            {
                Title = "Unsend message?",
                Content = "Unsending will remove the message for everyone",
                CloseButtonText = "Cancel",
                PrimaryButtonText = "Unsend",
                DefaultButton = ContentDialogButton.Primary,
            };
            var confirmation = await confirmDialog.ShowAsync();
            if (confirmation == ContentDialogResult.Primary)
            {
                await ViewModel.ChatService.Unsend(Item);
            }
        }

        private async void StoryShareOwnerLink_OnClick(Hyperlink sender, HyperlinkClickEventArgs args)
        {
            if (Item.Source.StoryShareMedia == null) return;
            var uri = new Uri($"https://www.instagram.com/{Item.Source.StoryShareMedia.OwnerUsername}/");
            await Launcher.LaunchUriAsync(uri);
        }

        private void ReplyToItem_OnClick(object sender, RoutedEventArgs e)
        {
            Item.Parent.ReplyingItem = Item;
            if (Window.Current.Content.FindDescendant("MessageTextBox") is Control control)
            {
                control.Focus(FocusState.Programmatic);
            }
        }

        private async void AddReactionMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            var emoji = await EmojiPicker.ShowAsync(MainContentControl,
                new FlyoutShowOptions { Placement = Item.FromMe ? FlyoutPlacementMode.Left : FlyoutPlacementMode.Right });

            if (string.IsNullOrEmpty(emoji)) return;

            await ViewModel.ChatService.ReactToItem(Item, emoji);
        }

        private async void RemoveReactionMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            if (!Item.ObservableReactions.MeLiked) return;

            await ViewModel.ChatService.RemoveReactionToItem(Item);
        }

        private async void DownloadMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            var url = Item?.VideoUri != null ? Item.VideoUri : Item?.FullImageUri;
            if (url == null)
            {
                return;
            }

            await MediaHelpers.DownloadMedia(url).ConfigureAwait(false);
        }
    }
}
