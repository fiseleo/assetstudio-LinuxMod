using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AssetStudio
{
    public class BuildType
    {
        private string buildType;

        public BuildType(string type)
        {
            buildType = type;
        }

        /// <summary>Release order of the build type: a(lpha) &lt; b(eta) &lt; f(inal) &lt; p(atch); unknown types sort as final.</summary>
        public int Order => buildType switch { "a" => 0, "b" => 1, "c" => 2, "f" => 3, "p" => 4, "x" => 3, _ => 3 };

        public bool IsAlpha => buildType == "a";
        public bool IsPatch => buildType == "p";
    }
}
