// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.

using NINA.Equipment.Interfaces.Mediator;
using OatAutoPA.Core;
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace OatAutoPA.Nina {

    /// <summary>
    /// Talks to the OAT firmware through the telescope N.I.N.A. is already connected to, so the plugin
    /// never opens the serial port itself. The OAT ASCOM driver passes <c>CommandBlind</c>/<c>CommandString</c>
    /// straight to the port; the suffix tells it what reply to wait for: <c>#</c> none, <c>#,#</c> a
    /// '#'-terminated string (see SharedResources.SendMessage in OpenAstroTracker-Desktop).
    /// </summary>
    public sealed class TelescopeMediatorMount : IOatMount {
        private readonly ITelescopeMediator telescope;
        private readonly SemaphoreSlim serial = new SemaphoreSlim(1, 1);

        public TelescopeMediatorMount(ITelescopeMediator telescope) {
            this.telescope = telescope ?? throw new ArgumentNullException(nameof(telescope));
        }

        public bool IsConnected {
            get {
                try { return telescope.GetInfo()?.Connected == true; } catch { return false; }
            }
        }

        public Task MoveAzimuthAsync(double arcMinutes, CancellationToken token) =>
            BlindAsync(":MAZ" + Format(arcMinutes) + "#", token);

        public Task MoveAltitudeAsync(double arcMinutes, CancellationToken token) =>
            BlindAsync(":MAL" + Format(arcMinutes) + "#", token);

        public async Task<AzAltMotion?> GetMotionAsync(CancellationToken token) {
            if (!IsConnected) { return null; }
            await serial.WaitAsync(token).ConfigureAwait(false);
            try {
                var reply = await Task.Run(() => telescope.SendCommandString(":GX#,#", true), token).ConfigureAwait(false);
                return GxStatusParser.Parse(reply);
            } catch (OperationCanceledException) {
                throw;
            } catch {
                return null;
            } finally {
                serial.Release();
            }
        }

        public async Task StopAzAltAsync() {
            // The firmware has no AZ/ALT-only stop yet: :Q# stops every motor, including tracking, so
            // tracking is switched straight back on. Replace with an AZ/ALT stop once the firmware has one.
            await serial.WaitAsync().ConfigureAwait(false);
            try {
                await Task.Run(() => {
                    telescope.SendCommandBlind(":Q#", true);
                    telescope.SendCommandString(":MT1#,n", true);
                }).ConfigureAwait(false);
            } finally {
                serial.Release();
            }
        }

        public async Task<MountIdentity> IdentifyAsync(CancellationToken token) {
            string driver = null;
            try {
                var info = telescope.GetInfo();
                // e.g. "OpenAstroTracker Telescope (ASCOM.OpenAstroTracker.Telescope)"
                driver = string.IsNullOrWhiteSpace(info?.DeviceId) || info.DeviceId == info.Name
                    ? info?.Name
                    : $"{info.Name} ({info.DeviceId})";
            } catch {
            }

            await serial.WaitAsync(token).ConfigureAwait(false);
            try {
                var product = await QueryAsync(":GVP#,#", token).ConfigureAwait(false);
                var version = product == null ? null : await QueryAsync(":GVN#,#", token).ConfigureAwait(false);
                return new MountIdentity { ProductName = product, FirmwareVersion = version, DriverName = driver };
            } finally {
                serial.Release();
            }
        }

        /// <summary>
        /// One query; any failure (a driver without CommandString, a mount that does not understand the
        /// command, the OAT driver's "255" error marker) reads as no answer.
        /// </summary>
        private async Task<string> QueryAsync(string command, CancellationToken token) {
            try {
                var reply = await Task.Run(() => telescope.SendCommandString(command, true), token).ConfigureAwait(false);
                return MountIdentity.CleanReply(reply);
            } catch (OperationCanceledException) {
                throw;
            } catch {
                return null;
            }
        }

        private async Task BlindAsync(string command, CancellationToken token) {
            await serial.WaitAsync(token).ConfigureAwait(false);
            try {
                await Task.Run(() => telescope.SendCommandBlind(command, true), token).ConfigureAwait(false);
            } finally {
                serial.Release();
            }
        }

        /// <summary>The firmware parses with strtod: plain invariant decimals, explicit sign.</summary>
        internal static string Format(double arcMinutes) =>
            arcMinutes.ToString("+0.00;-0.00;+0.00", CultureInfo.InvariantCulture);
    }
}
