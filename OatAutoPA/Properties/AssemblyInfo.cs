using System.Reflection;
using System.Runtime.InteropServices;

// [MANDATORY] Unique identifier of the plugin. Also the broker identity TPPA binds a run to: never change it.
[assembly: Guid("9029e272-cc20-4c3f-89dd-a7ec5c2605f8")]

[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]

// [MANDATORY] Name of the plugin; the options template key is "<AssemblyTitle>_Options".
[assembly: AssemblyTitle("OAT AutoPA")]
[assembly: AssemblyDescription("Automatic polar alignment for OpenAstroTracker / OpenAstroMount AZ/ALT motors, driven by Three Point Polar Alignment.")]

[assembly: AssemblyCompany("Kekimurus")]
[assembly: AssemblyProduct("OAT AutoPA")]
[assembly: AssemblyCopyright("Copyright © 2026 Kekimurus")]

// TPPA's external correction bridge needs TPPA 2.2.8.0, which needs N.I.N.A. 3.1.2.9001.
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.1.2.9001")]

[assembly: AssemblyMetadata("License", "MPL-2.0")]
[assembly: AssemblyMetadata("LicenseURL", "https://www.mozilla.org/en-US/MPL/2.0/")]
[assembly: AssemblyMetadata("Repository", "https://github.com/Kekimurus/nina.plugin.oatautopa")]
[assembly: AssemblyMetadata("Homepage", "https://github.com/Kekimurus/nina.plugin.oatautopa")]
[assembly: AssemblyMetadata("Tags", "Polar Alignment,OpenAstroTracker,OpenAstroMount,OAT,OAM,TPPA")]
[assembly: AssemblyMetadata("ChangelogURL", "https://github.com/Kekimurus/nina.plugin.oatautopa/blob/main/CHANGELOG.md")]
[assembly: AssemblyMetadata("FeaturedImageURL", "")]
[assembly: AssemblyMetadata("ScreenshotURL", "")]
[assembly: AssemblyMetadata("AltScreenshotURL", "")]
[assembly: AssemblyMetadata("LongDescription", @"Drives the AZ/ALT motors of an OpenAstroTracker or OpenAstroMount during Three Point Polar Alignment.

TPPA (2.2.8.0 or later) measures; this plugin moves the axes through the mount N.I.N.A. is already connected to, holding TPPA's capture while the motors turn so no frame is wasted.

Every correction also calibrates the mount: the plugin learns how far each axis really moves per commanded arcminute and how much backlash it has, refuses numbers that do not repeat (slippage), and can detect a reversed axis.

Setup: in TPPA set 'Polar Alignment System' to 'External: OAT AutoPA', set a non-zero tolerance and run TPPA from a sequence.")]

[assembly: ComVisible(false)]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
