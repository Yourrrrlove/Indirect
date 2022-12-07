﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;
using Windows.Networking.Sockets;
using InstagramAPI;
using InstagramAPI.Push;
using InstagramAPI.Utils;

namespace BackgroundPushClient
{
    public sealed class SocketActivity : IBackgroundTask
    {
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private BackgroundTaskCancellationReason _cancellationReason;

        public async void Run(IBackgroundTaskInstance taskInstance)
        {
            Instagram.StartSentry();
            taskInstance.Canceled += TaskInstanceOnCanceled;
            this.Log("-------------- Start of background task --------------");
            var details = (SocketActivityTriggerDetails) taskInstance.TriggerDetails;
            var socketId = details.SocketInformation.Id;
            this.Log($"{details.Reason} - {socketId}");
            FileStream lockFile = null;
            var deferral = taskInstance.GetDeferral();
            try
            {
                if (_cancellation.IsCancellationRequested || string.IsNullOrEmpty(socketId) ||
                    socketId.Length <= PushClient.SocketIdPrefix.Length)
                {
                    return;
                }

                lockFile = await Utils.TryAcquireSocketActivityLock(socketId);
                if (lockFile == null)
                {
                    return;
                }

                var sessionName = socketId.Substring(PushClient.SocketIdPrefix.Length);
                var session = await SessionManager.TryLoadSessionAsync(sessionName);
                if (session == null)
                {
                    if (details.Reason == SocketActivityTriggerReason.SocketClosed)
                    {
                        return;
                    }

                    throw new Exception($"{nameof(SocketActivity)} triggered without session.");
                }

                var instagram = new Instagram(session);
                var utils = new Utils(instagram);
                instagram.PushClient.MessageReceived += utils.OnMessageReceived;
                instagram.PushClient.ExceptionsCaught += Utils.PushClientOnExceptionsCaught;
                switch (details.Reason)
                {
                    case SocketActivityTriggerReason.KeepAliveTimerExpired:
                    case SocketActivityTriggerReason.SocketActivity:
                    {
                        try
                        {
                            var socket = details.SocketInformation.StreamSocket;
                            await instagram.PushClient.StartWithExistingSocket(socket);
                        }
                        catch (Exception)
                        {
                            return;
                        }

                        break;
                    }
                    case SocketActivityTriggerReason.SocketClosed:
                    {
                        await Task.Delay(TimeSpan.FromSeconds(3), _cancellation.Token);
                        if (!await Utils.TryAcquireSyncLock(session.SessionName))
                        {
                            this.Log("Main application is running.");
                            return;
                        }

                        try
                        {
                            var socket = details.SocketInformation.StreamSocket;
                            socket?.Dispose();
                        }
                        catch (Exception)
                        {
                            // pass
                        }

                        try
                        {
                            await instagram.PushClient.StartFresh(taskInstance);
                        }
                        catch (Exception)
                        {
                            // Most common is "No such host is known"
                            return;
                        }

                        break;
                    }
                    default:
                        return;
                }

                await Task.Delay(TimeSpan.FromSeconds(PushClient.WaitTime));
                await instagram.PushClient.TransferPushSocket();
                await SessionManager.SaveSessionAsync(instagram, true);
                instagram.PushClient.MessageReceived -= utils.OnMessageReceived;
                instagram.PushClient.ExceptionsCaught -= Utils.PushClientOnExceptionsCaught;
            }
            catch (TaskCanceledException)
            {
                Utils.PopMessageToast($"{nameof(SocketActivity)} cancelled: {_cancellationReason}");
            }
            catch (Exception e)
            {
                Utils.PopMessageToast($"[{details.Reason}] {e}");
                DebugLogger.LogException(e, properties: new Dictionary<string, string>
                {
                    {"SocketActivityTriggerReason", details.Reason.ToString()},
                    {"Cancelled", _cancellation.IsCancellationRequested ? _cancellationReason.ToString() : string.Empty}
                });
                this.Log($"{typeof(SocketActivity).FullName}: Can't finish push cycle. Abort.");
            }
            finally
            {
                await Sentry.SentrySdk.FlushAsync(TimeSpan.FromSeconds(2));
                taskInstance.Canceled -= TaskInstanceOnCanceled;
                lockFile?.Dispose();
                _cancellation.Dispose();
                this.Log("-------------- End of background task --------------");
                deferral.Complete();
            }
        }

        private void TaskInstanceOnCanceled(IBackgroundTaskInstance sender, BackgroundTaskCancellationReason reason)
        {
            _cancellationReason = reason;
            _cancellation?.Cancel();
        }
    }
}
