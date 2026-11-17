// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.

using System.ComponentModel.Composition;
using System.Windows;

namespace OatAutoPA {

    [Export(typeof(ResourceDictionary))]
    partial class Options : ResourceDictionary {

        public Options() {
            InitializeComponent();
        }
    }
}
