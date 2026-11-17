// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.

namespace OatAutoPA.Core {

    /// <summary>
    /// Parses the <c>:GX#</c> reply of the OAT firmware: <c>State,motion,ra,dec,trk,raCoord,decCoord,...#</c>.
    /// The second field is six characters, one per motor (<c>RDTZAF</c>), with '-' when that motor is idle;
    /// index 3 is azimuth (<c>Z</c>/<c>z</c>) and index 4 altitude (<c>A</c>/<c>a</c>). Same reading as
    /// OATControl's <c>NinaPolarAlignLogProcessor</c>.
    /// </summary>
    public static class GxStatusParser {
        public static AzAltMotion? Parse(string reply) {
            if (string.IsNullOrWhiteSpace(reply)) { return null; }
            var parts = reply.Trim().TrimEnd('#').Split(',');
            if (parts.Length < 2) { return null; }
            var motion = parts[1];
            if (motion.Length < 5) { return null; }
            return new AzAltMotion(motion[3] != '-', motion[4] != '-');
        }
    }
}
