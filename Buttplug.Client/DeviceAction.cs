﻿using JetBrains.Annotations;

namespace Buttplug.Client
{
    public class DeviceEventArgs
    {
        public enum DeviceAction
        {
            ADDED,
            REMOVED,
        }

        [NotNull]
        private readonly ButtplugClientDevice device;

        [NotNull]
        private readonly DeviceAction action;

        public DeviceEventArgs(ButtplugClientDevice aDevice, DeviceAction aAction)
        {
            device = aDevice;
            action = aAction;
        }
    }
}