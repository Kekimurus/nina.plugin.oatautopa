// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.

using CommunityToolkit.Mvvm.Input;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using OatAutoPA.Core;
using OatAutoPA.Nina;
using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace OatAutoPA {

    /// <summary>
    /// Plugin manifest and options. Hosts the external correction controller that TPPA lists as
    /// "External: OAT AutoPA" in its Polar Alignment System selection.
    /// </summary>
    [Export(typeof(IPluginManifest))]
    public class OatAutoPAPlugin : PluginBase, INotifyPropertyChanged, ICalibrationStore {
        /// <summary>Name TPPA shows and addresses the controller by. Keep it stable: TPPA remembers the selection.</summary>
        public const string ControllerName = "OAT AutoPA";

        private readonly IPluginOptionsAccessor options;
        private readonly BrokerTransport transport;
        private readonly OatController controller;

        [ImportingConstructor]
        public OatAutoPAPlugin(IProfileService profileService, IMessageBroker messageBroker, ITelescopeMediator telescopeMediator) {
            var pluginId = Guid.Parse(Identifier);
            options = new PluginOptionsAccessor(profileService, pluginId);
            profileService.ProfileChanged += (_, _) => RaiseAllChanged();

            transport = new BrokerTransport(messageBroker, pluginId, ControllerName);
            controller = new OatController(transport,
                                           new TelescopeMediatorMount(telescopeMediator),
                                           ReadSettings,
                                           this,
                                           new NinaLog(),
                                           ControllerName,
                                           GetType().Assembly.GetName().Version?.ToString() ?? "1.0");
            controller.StatusChanged += _ => {
                RaisePropertyChanged(nameof(ControllerStatus));
                RaisePropertyChanged(nameof(CalibrationSummary));
            };
            ResetCalibrationCommand = new RelayCommand(() => {
                controller.ResetCalibration();
                RaisePropertyChanged(nameof(CalibrationSummary));
            });
            controller.Start();
        }

        public override Task Teardown() {
            controller.Dispose();
            transport.Dispose();
            return base.Teardown();
        }

        // ------------------------------------------------------------------ status

        public string ControllerStatus => controller.Status;
        public string CalibrationSummary => controller.DescribeCalibration();
        public ICommand ResetCalibrationCommand { get; }

        // ------------------------------------------------------------------ options (per profile)

        public bool Enabled { get => GetBool(true); set => SetBool(value); }
        public double Gain { get => GetDouble(0.8); set => SetDouble(Math.Clamp(value, 0.1, 1.0)); }
        public double MaxMoveArcMin { get => GetDouble(30); set => SetDouble(Math.Clamp(value, 1, 600)); }
        public double ManualAdjustLimitArcMin { get => GetDouble(120); set => SetDouble(Math.Max(1, value)); }
        public double SettleSeconds { get => GetDouble(1.5); set => SetDouble(Math.Clamp(value, 0, 60)); }
        public bool InvertAzimuth { get => GetBool(false); set => SetBool(value); }
        public bool InvertAltitude { get => GetBool(false); set => SetBool(value); }
        public bool AutoDetectDirection { get => GetBool(true); set => SetBool(value); }
        public bool UseLearnedResponse { get => GetBool(true); set => SetBool(value); }
        public bool UseBacklashCompensation { get => GetBool(true); set => SetBool(value); }
        public bool PersistCalibration { get => GetBool(true); set => SetBool(value); }
        public double MaxResponseScatter { get => GetDouble(0.25); set => SetDouble(Math.Clamp(value, 0.05, 1.0)); }
        public double MinLearningMoveArcMin { get => GetDouble(1.5); set => SetDouble(Math.Max(0.1, value)); }
        public double MoveTimeoutSeconds { get => GetDouble(180); set => SetDouble(Math.Clamp(value, 10, 1800)); }

        private ControllerSettings ReadSettings() => new ControllerSettings {
            Enabled = Enabled,
            Gain = Gain,
            MaxMoveArcMin = MaxMoveArcMin,
            ManualAdjustLimitArcMin = ManualAdjustLimitArcMin,
            SettleSeconds = SettleSeconds,
            InvertAzimuth = InvertAzimuth,
            InvertAltitude = InvertAltitude,
            AutoDetectDirection = AutoDetectDirection,
            UseLearnedResponse = UseLearnedResponse,
            UseBacklashCompensation = UseBacklashCompensation,
            PersistCalibration = PersistCalibration,
            MaxResponseScatter = MaxResponseScatter,
            MinLearningMoveArcMin = MinLearningMoveArcMin,
            MoveTimeoutSeconds = MoveTimeoutSeconds,
        };

        // ------------------------------------------------------------------ ICalibrationStore (per profile)

        AxisCalibrationSnapshot ICalibrationStore.Load(Axis axis) {
            // -1 marks "not learned" (the profile store has no nullable values).
            var response = options.GetValueDouble(axis + "Response", -1);
            var backlash = options.GetValueDouble(axis + "Backlash", -1);
            return new AxisCalibrationSnapshot {
                Response = response > 0 ? response : null,
                BacklashArcMin = backlash >= 0 ? backlash : null
            };
        }

        void ICalibrationStore.Save(Axis axis, AxisCalibrationSnapshot snapshot) {
            options.SetValueDouble(axis + "Response", snapshot?.Response ?? -1);
            options.SetValueDouble(axis + "Backlash", snapshot?.BacklashArcMin ?? -1);
            RaisePropertyChanged(nameof(CalibrationSummary));
        }

        void ICalibrationStore.SaveDirectionInverted(Axis axis, bool inverted) {
            if (axis == Axis.Azimuth) { InvertAzimuth = inverted; } else { InvertAltitude = inverted; }
        }

        // ------------------------------------------------------------------ helpers

        private bool GetBool(bool fallback, [CallerMemberName] string name = null) => options.GetValueBoolean(name, fallback);

        private void SetBool(bool value, [CallerMemberName] string name = null) {
            options.SetValueBoolean(name, value);
            RaisePropertyChanged(name);
        }

        private double GetDouble(double fallback, [CallerMemberName] string name = null) => options.GetValueDouble(name, fallback);

        private void SetDouble(double value, [CallerMemberName] string name = null) {
            options.SetValueDouble(name, value);
            RaisePropertyChanged(name);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void RaisePropertyChanged([CallerMemberName] string propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        private void RaiseAllChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

        private sealed class NinaLog : ILog {
            public void Debug(string message) => Logger.Debug(message);
            public void Info(string message) => Logger.Info(message);
            public void Warning(string message) => Logger.Warning(message);

            public void Error(string message) {
                Logger.Error(message);
                Notification.ShowWarning($"{ControllerName}: {message}");
            }
        }
    }
}
