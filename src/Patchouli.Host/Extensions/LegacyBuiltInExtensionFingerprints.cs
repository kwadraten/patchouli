namespace Patchouli.Host.Extensions;

/// <summary>
///     Fingerprints of built-in extension packages shipped by earlier releases or written to the
///     runtime working directories by earlier application versions (including packages carrying the
///     retired <c>extension.yml</c> metadata format). A working copy or persisted generation whose
///     fingerprint appears here is byte-identical to a previously bundled package, so adopting the
///     current bundled package discards no user work. User-edited copies produce fingerprints that
///     are never in this list and stay untouched.
/// </summary>
/// <remarks>
///     Before shipping a release whose bundled package sources changed, append the outgoing
///     package fingerprints (<c>BuiltIn*Extensions.Packages</c> of the previous build, plus any
///     historical working-copy variants such as retired <c>extension.yml</c> packages or
///     line-ending variants) so the upgrade keeps adopting byte-identical built-in copies.
/// </remarks>
internal static class LegacyBuiltInExtensionFingerprints
{
    private static readonly Dictionary<string, string[]> Known = new(StringComparer.Ordinal)
    {
        ["metadata.arxiv"] =
        [
            "3bfc22716af035b0601cba2835ddaa5da39d3dfba4ae481d2fa35716d9d69da7",
            "26a59a3df95c5f382882fd57eda69a7c46f099ed92e1bbf3e050a310aeddcb79",
            "8bf55178ab63ddcced9d20c28908015fdc76107050c36ac11b720fa75057b295"
        ],
        ["metadata.bnf"] =
        [
            "34c42654cabcbd1a679c8d6f8a83d5ce791980ced1dacffee190362579b25c7b",
            "7b97e05636ffacb1bedf5367b7a99ef118606a357e8ff0aafadf4ab53e3089f4",
            "23f9c3d248131128f4f836d2142035a2084cb925e3f8580d617b943b54caaca9"
        ],
        ["metadata.calis"] =
        [
            "685fa5d07f8711a3a43e7a79e0daddd6daccdaf97e68cb3227d6290addad884c",
            "9a7e1789ae199dded439411f9e733b45b2dbc5ed54c790e825ba6cb3e5796502",
            "8dde1f33096bde7cf927388b78f3e333318e17dadb5e1f098cbc4da519d59d57"
        ],
        ["metadata.cinii"] =
        [
            "192739064d0d8b094f1af4e7f3d2348a0e3f91a48e94b131419856dbee4b5d84",
            "895aaa64223b51458f1ba7ee3299c1e4e8b077251cb77dc0748e88391881baed",
            "a9aac4392774538fe777dca0ac02388f3d03203b639a328b2c3f28be174e2dec"
        ],
        ["metadata.crossref"] =
        [
            "61b099783f0018fcceb223132d02ee8101dbb1554f7f037dfb0e92d097dd13a0",
            "72141bcd7ef7f77f5b897f204cdf3c3a37042c3c4303721761ab6c3506285fb5",
            "1404df51aaa419e6b794ae9a6e3e0a0be85c479ed7cab02fb6e789212692320a"
        ],
        ["metadata.datacite"] =
        [
            "a0d3570d2b19f2cd8576ff7570aedb667d33615619a94469d5796f833396bdb1",
            "da22ec631d28d4e5df5e2858c8e44b78aeb6d1522f346c0d7ac60464bd8b8dc0",
            "8be733df5134cc2e37e55c78945b0a33fc9d608114e28a63697d5bbdb2e4d292"
        ],
        ["metadata.dnb"] =
        [
            "e0d67dd7c510db65d6723020956a662d4ae5936c6f96cb09b3679bc197bb435b",
            "d326ba71cffd9fef006c24aff9af6a6bc3b53e87f8fa9d12e9572bdaab1a8921",
            "4525386a28563bc3030cc932de7f790103dde5df61525bea610453ac5b62023f"
        ],
        ["metadata.google-books"] =
        [
            "d6f68619ed2eb2424f4f2c18b0d4e367e2816010f928b077e223158101cb0b03",
            "8d2fb51bdbf4e23b247de3e0c0295149c4de6902db07d58c407a3f83f146d897",
            "dc542614b6fcedd24e46b86ff56ea91c5a3adf748bc858398b26ff19b4bb3203"
        ],
        ["metadata.library-of-congress"] =
        [
            "e1d427fee782653f6288443036b370894d7883c8411269937d6244aa2e949a59",
            "13f227c8945bfe8975680d9f4bc782bee470c02a7478adc4f7e0cdbd08b587e5",
            "dd7d5b1319e44c8fa9be3c5ba17931ae5ff3f38ffe661e741ed705499d07b85d"
        ],
        ["metadata.ndl"] =
        [
            "92c5b001ac8a6fdf16941822232436fef9ca5df34db4b6b474dfc0500af5779c",
            "1814e24b45f8f92758dd8a7d5f6ed76646e1db664a1e47c7a867dd91c2127814",
            "184d69c4cb4d3b9c9923517bb1a7f7a32080f70b2bf10134b89d08c19a2f97f8"
        ],
        ["metadata.nlc"] =
        [
            "2e55a26b2c8fbbd1a5a12c9e9aa40c183275b34610b3c8d9042164df66e08ba6",
            "b99de4b65d9be57a043276bd62515ee44ae6963233d91dfabf47f71202cf0360",
            "3b136369a7d3d7cbc18c22868356711c060d1e7f5eb1d29fb86f153df17fd6c4"
        ],
        ["metadata.open-library"] =
        [
            "e75f352ab330f9adef01abf7cd3e066265842e6198dd79deb6d352169439e771",
            "35fe43ba50656c7a31ed3d9b906b9b45622497766b975ef5b02fef33cef013ff",
            "e0e28d4c9b9400ffb8d438e37aed7c769a54d949bbb583a140416f166f47d5c4"
        ],
        ["metadata.openalex"] =
        [
            "309783cac10b739de0c84977eb4a989bb5995b8a138e2bbbb6642e8a26aac2e8",
            "f9f42ab2456b44b0c516e54186d9fb2a8fba3c5cdf7cf612755d97b6db5eb5e0",
            "8f33f33433c8168777938f23c1849386fa40f13e0e8dfec3e9f6809439aeee26"
        ],
        ["metadata.pmc-id-converter"] =
        [
            "d9f5bd41335668c8a187484c7cde62676918bbda63b3fb9fb833e3eee6c7a4fd",
            "779b18c2c50f2a3bb395b81fe2d3140447e7f06d7fd8daa8a545724bd9db3ff2",
            "3397051460fc0aff55637f9ddd5e2bf4fbd1645c5d6e6c092ff6163fb33ea4fb"
        ],
        ["metadata.pubmed"] =
        [
            "29e2ea0d6fd16aec18d54132c8d3949629dbcb596ce9933f32d50d1c283c66f0",
            "b5d3dee27a8443d7f140de1024c219c3a11e00f2069f2c46c6b4d34b6b26497b",
            "0bc2e03adb3a4b8eeae31c6b8ed1d51d5b94c81f086689fee18dd6c891e479b5"
        ],
        ["metadata.semantic-scholar"] =
        [
            "531c1f97033a745332d6c35828a6836ca9f92b5dd92ae9d25008fc902b51f45e",
            "2bf70cf2c714c553133ff647411dfed1138dbe25fa0853c7e4ee6d97d62ee90a",
            "342fc6df8c8b423a57a38cb9884ec8ea142236edd31a14929a3e0cced7fbe9f9"
        ],
        ["ocr.multimodal"] =
        [
            "847f78cf3e69f8d4618337f2a9a20f8269465a35ad3403b37ba3ad6270a106fb"
        ],
        ["ocr.ndlkotenocr-lite"] =
        [
            "013bdd7abec02e1e8dcf117c625cdbdb523eed703b4310f40b0b928f0be2b4c5"
        ],
        ["ocr.ndlocr-lite"] =
        [
            "ee2963dedf7c8ff3661892615a0a6640c4deefcbfeb8d28657e4381902e9d9c8"
        ],
        ["ocr.rapidocr"] =
        [
            "f746f103ebaece65da99176cb6d0e36ef250a89de4932a5d040a9ed6f18c7cf4"
        ],
        ["pdf-text.native-gaptree"] =
        [
            "497019dd7b8b5f9c3a014d97fef1b8200a5e74be9f6f81aa6f7489ef536b58f7"
        ],
        ["pdf-text.native-xycut"] =
        [
            "026b3d0f087a63c1a88337a7b2ef6034274d10961382e7d7a2d182f11b83a800"
        ],
        ["pdf-text.visual-guided"] =
        [
            "52e25058c632aacaaf5518e39852df732a0343dc775539ba38343fe90ecb4527"
        ],
        ["translation.deepl-free"] =
        [
            "0ad9d151ccb39543c54100efc9c428e2ad7f467040b819252bd3ed90d61d3b6d",
            "08422c0ab0c2fc66fad5a241ec954980d2d39e8c24578a37bbfd042d0c83a805",
            "b6439e37483d0ddf682b0d549951b88c4902c227751d4b7c88973c1f6463f6c8",
            "0c0ce9d14c2762c453b5cc317dd977d1e3e5bebf45ba949f9375959664af119c",
            "cedece29b39eacfa0c265dd7adef92a92adce88be6955b39c5373a211b73f6f9"
        ],
        ["translation.deeplx"] =
        [
            "ff1f4ef46a7402bc93830ed55d76d5925d882c37cecc44f53e984811c39628a0",
            "ad9a1c3c9dbf937bfacc31aa4953cdabf84569300c80d51da2d93aeec080ed46",
            "155782e0259c33e6a7c32d392fecf3b11a0a674dc33f44bae44c6cfa9375a516",
            "e106a39278f1da275ee4dcf6d1c5328e91f6aa4c47502703dceb44dccf5d3920",
            "4807476f47469d57b8227ed7156656418b5e4dead39c6ed0949b5c39ee0d7b79"
        ],
        ["translation.google-free"] =
        [
            "254b96eb1f0160a95c9d86d9713ffc75ca3fd60e3a10dfb84eccdc3a0a7c49ce",
            "368ba22aa265c0bbfe82648c8f1212ba4c669c98910d52b424d8f2808680d1a5",
            "02efc26fab404b2694514ba5b72075d36e5f9d6705f78cc87f15cd80cd930ff2",
            "89f0a155fd3aa27fbc334e0abc7183b4fccf11dde91ee3bb1788d36faf3d3823",
            "bcbd00e4fba56ac3603455b75faf3f49f5838c2568234f29ea611899d828a08a"
        ],
        ["translation.llm"] =
        [
            "46d572e9bfa3da29edfae68d3168e32e7777308c53f76eb6d3e79b628f16c496",
            "eac6bb7d5710bc994020a24ecd433b40ec7a1031525b9b9033c8d4c81096e5da",
            "30a6e0e203e99be26c0cdaee272fcbb5ec07e84f18b0e850c5bd6d9f40910dbd",
            "126ba8a4433eee3d47687a899b6951309adcd8767e08b7ccb770fcb49009d2b6",
            "0235bbfb70cbc327fc65afe29b914222dd3be0f90b8d0657467286a71b32615a"
        ],
        ["translation.microsoft-free"] =
        [
            "1c88d3784950973b58061c575b9da6b48d7cec6800ff060880376e532a2e4813",
            "180a3c236b14a05071f51bf43406c4e6732514fd3bb3d1d0cc2cb7bcdbad879f",
            "e419feb7b7d8f129115d45c3c404a9df1175c30ec3508e8d7faa3787cb4699a1",
            "3a362e030d3827c160ba2dc95e1a6514233b3127bbd8c5b1d259cb4180510d2f"
        ],
        ["translation.tencent-free"] =
        [
            "97292625f7138be12de0d8a1b2222666f63502ba48fe3acde92839a27864190e",
            "48c4f1ba7b5ee68520072a42c3145ad77c752fbadbaf7094ce0df536f146368f",
            "d2d7a288c21030cee649e0b1c21dfa8ea972371ad6e5de850110d0a0a13fbd95",
            "9cb4bc5d19c6664462e859b2662d86c48f7be994bd750bf52351cab9daec26a8"
        ]
    };

    public static bool IsKnownLegacy(string id, string fingerprint)
    {
        return Known.TryGetValue(id, out string[]? fingerprints) &&
               Array.Exists(fingerprints, known => string.Equals(known, fingerprint, StringComparison.Ordinal));
    }
}
