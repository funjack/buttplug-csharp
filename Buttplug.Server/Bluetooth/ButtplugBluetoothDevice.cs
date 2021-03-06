﻿using System;
using Buttplug.Core;
using JetBrains.Annotations;

namespace Buttplug.Server.Bluetooth
{
    public class ButtplugBluetoothDevice : ButtplugDevice
    {
        [NotNull]
        protected readonly IBluetoothDeviceInterface Interface;

        protected ButtplugBluetoothDevice([NotNull] IButtplugLogManager aLogManager,
            [NotNull] string aName,
            [NotNull] IBluetoothDeviceInterface aInterface)
            : base(aLogManager,
                   aName,
                   aInterface.GetAddress().ToString())
        {
            Interface = aInterface;
            Interface.DeviceRemoved += DeviceRemovedHandler;
        }

        public override void Disconnect()
        {
            Interface.Disconnect();
        }

        private void DeviceRemovedHandler(object aObject, EventArgs aEvent)
        {
            InvokeDeviceRemoved();
            Interface.DeviceRemoved -= DeviceRemovedHandler;
        }
    }
}
