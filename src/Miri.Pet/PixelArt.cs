namespace Miri.Pet;

/// <summary>
/// Procedural 32x32 pixel art for Miri, built from ASCII maps.
/// Map chars: '.' / '_' / ' ' = leave as is, 'x' = erase (transparent),
/// 'w' fur, 's' shade, 'o' outline, 'e' eye, 'g' eye rim, 'h' sparkle, 'p' pupil, 'n' nose / inner ear.
/// </summary>
internal static class PixelArt
{
    public const int Size = 32;

    public static uint ColorOf(char c) => c switch
    {
        'w' => 0xFFF4F4F2, // fur
        's' => 0xFFC9CDD6, // shade
        'o' => 0xFF5A5E6B, // outline
        'e' => 0xFF7ED957, // eyes (bright green iris)
        'g' => 0xFF2F7A3B, // eye rim (deep green)
        'h' => 0xFFFFFFFF, // eye sparkle
        'p' => 0xFF1E1E1E, // pupil
        'n' => 0xFFE8A9A6, // nose / inner ear
        'r' => 0xFFFF6F91, // little hearts
        'y' => 0xFFFFD84A, // sparkle
        'b' => 0xFF4F8FD8, // food bowl
        'l' => 0xFF8FC1EE, // food bowl highlight
        'k' => 0xFFC8854A, // kibble
        'j' => 0xFF8A5530, // kibble (dark)
        _ => 0x00000000,
    };

    // ------------------------------------------------------------------ components

    /// <summary>Front-facing head (closed chin), absolute rows 0..17.</summary>
    private static readonly string[] Head =
    {
        "................................", // 0
        "................................", // 1
        "................................", // 2
        "......o..............o..........", // 3
        "......oo............oo..........", // 4
        "......ono..........ono..........", // 5
        "......onno........onno..........", // 6
        "......onnnoooooooonnno..........", // 7
        ".....owwwwwwwwwwwwwwwso.........", // 8
        ".....owwwggwwwwwwggwwso.........", // 9  big round green eyes
        ".....owwghpgwwwwghpgwso.........", // 10
        ".....owwgeegwwwwgeegwso.........", // 11
        ".....owwwggwwwwwwggwwso.........", // 12
        "...ssownwwwwwnnwwwwwnsoss.......", // 13  nose, pink cheeks, whiskers
        ".....owwwwwowoowowwwwso.........", // 14
        "....sowwwwwwowwowwwwwsos........", // 15  cute "w" mouth
        "......owwwwwwwwwwwwwso..........", // 16
        ".......oooooooooooooo...........", // 17  chin
    };

    /// <summary>Sitting body, absolute rows 17..31 (opens the chin into the chest).</summary>
    private static readonly string[] SitBody =
    {
        ".......ooowwwwwwwwooo...........", // 17
        "........owwwwwwwwwso............", // 18
        "........owwwwwwwwwso............", // 19
        "........owwwwwwwwwso............", // 20
        ".......owwwwwwwwwwwso.....oo....", // 21
        ".......owwwwwwwwwwwso....owwo...", // 22
        "......owwwwwwwwwwwwwso...owwso..", // 23
        "......owwwwwwwwwwwwwso...owwso..", // 24
        ".....owwswwwwwwwwswwwso..owwso..", // 25
        ".....owwwswwwwwwswwwwso..owwso..", // 26
        ".....owwwswwwwwwswwwwso..owwso..", // 27
        ".....owwwswwowwwswwwwso..owwso..", // 28
        ".....owwwswwowwwswwwwsoooowwwso.", // 29
        ".....owwwswsowswswwwwswwwwwwso..", // 30
        "......ooooooooooooooooooooooo...", // 31
    };

    // Tail tip flicked to the right (x=24, y=20).
    private static readonly string[] TailFlick =
    {
        "....oo",  // 20
        "..xowwo", // 21
        "..owwso", // 22
    };
    private static readonly string[] EyesBlink =
    {
        "wwww____wwww",
        "wwww____wwww",
        "oooo____oooo",
        "wwww____wwww",
    };
    private static readonly string[] EyesNarrow =
    {
        "wwww____wwww",
        "oooo____oooo",
        "gepg____gepg",
        "wggw____wggw",
    };
    private static readonly string[] EyesHappy = // closed, content
    {
        "wwww____wwww",
        "woow____woow",
        "owwo____owwo",
        "wwww____wwww",
    };
    private static readonly string[] EyesLookRight =
    {
        "wggw____wggw",
        "ghpp____ghpp",
        "geep____geep",
        "wggw____wggw",
    };
    private static readonly string[] EyesSleep =
    {
        "wwww____wwww",
        "wwww____wwww",
        "woow____woow",
        "wwww____wwww",
    };
    private static readonly string[] EyesWide = // confused: one eye opens wider
    {
        "gggg____wggw",
        "ghpg____ghpg",
        "gppg____geeg",
        "gggg____wggw",
    };

    private static readonly string[] MouthOpen =
    {
        "woooow",
        "wonnow",
        "wwooww",
    };

    private static readonly string[] EarTwitchLeft = // x=4, y=3
    {
        "xxxxxx",
        "xoxxxx",
        "xonoox",
        "xonnno",
    };

    private static readonly string[] Blush = { "n" };

    private static readonly string[] ThinkDots2 = { "oo.oo", "oo.oo" };
    private static readonly string[] ThinkDots3 = { "oo.oo.oo", "oo.oo.oo" };

    private static readonly string[] Question =
    {
        ".oo.",
        "o..o",
        "...o",
        "..o.",
        "....",
        "..o.",
    };

    // Raised right paw (viewer's right), pointing up and out.
    private static readonly string[] PawRaised = // x=18, y=14
    {
        "......oo..",
        ".....owwo.",
        "....owwwo.",
        "...owwwso.",
        "..owwwso..",
        ".owwwso...",
        "owwwso....",
    };

    // ------------------------------------------------------------- walking (side)

    private static readonly string[] WalkBody =
    {
        "....oooooooo....................", // 15
        "...owwwwwwwwo...................", // 16
        "..owwwwwwwwwwo..................", // 17
        "..owwwwwwwwwwwo.................", // 18
        ".owwwwwwwwwwwwwo................", // 19
        ".owwwwwwwwwwwwwwwwwwwwso........", // 20
        ".owwwwwwwwwwwwwwwwwwwwso........", // 21
        ".owwwwwwwwwwwwwwwwwwwwso........", // 22
        ".owwwwwwwwwwwwwwwwwwwwso........", // 23
        ".oswwwwwwwwwwwwwwwwwwsso........", // 24
        "..osswwwwwwwwwwwwwwwssso........", // 25
        "...oooooooooooooooooooo.........", // 26
    };

    private static readonly string[] LegNear = { "owwso", "owwso", "owwso", "owwso", "owwso", "owwwo", ".ooo." }; // rows 25..31
    private static readonly string[] LegFar = { "ossso", "ossso", "ossso", "ossso", "ossso", "ossso", ".ooo." };

    private static readonly string[] WalkTail = // x=0, y=6
    {
        "...oo.",
        "..owwo",
        "..owso",
        ".owso.",
        "owso..",
        "owso..",
        "owso..",
        "owso..",
        "owso..",
        ".owso.",
        "..owso",
        "..owso",
    };
    private static readonly string[] WalkTailFlick =
    {
        "....oo",
        "...owo",
        "..owso",
        ".owso.",
        "owso..",
        "owso..",
        "owso..",
        "owso..",
        "owso..",
        ".owso.",
        "..owso",
        "..owso",
    };
    // ------------------------------------------------------------- sleeping (loaf)

    private static readonly string[] Loaf =
    {
        "..............oooooooooo........", // 18
        "............oowwwwwwwwwwoo......", // 19
        "...........owwwwwwwwwwwwwwo.....", // 20
        "..........owwwwwwwwwwwwwwwso....", // 21
        "..........owwwwwwwwwwwwwwwwso...", // 22
        "..........owwwwwwwwwwwwwwwwwso..", // 23
        "..........owwwwwwwwwwwwwwwwwso..", // 24
        "..........owwwwwwwwwwwwwwwwwso..", // 25
        "..........owwwwwwwwwwwwwwwwwso..", // 26
        "..........owwwwwwwwwwwwwwwwwso..", // 27
        "..........owwwwwwwwwwwwwwwwwso..", // 28
        "..........owwwwwwwwwwwwwwwwwso..", // 29
        "..........owwwwwwwwwwwwwwwwsso..", // 30
        "...........oooooooooooooooooo...", // 31
    };
    private static readonly string[] LoafBreath = // y=17, raises the back by one pixel
    {
        "...............oooooooo.........",
        "..............owwwwwwwwo........",
    };
    private static readonly string[] SleepTail = // y=27
    {
        "...........................o....",
        "..............ooooooooooooowo...",
        "............oowwwwwwwwwwwwwso...",
        "............osssssssssssssso....",
        "............ooooooooooooooo.....",
    };
    private static readonly string[] SleepPaws = // y=29
    {
        "....owwwwowwwwo.",
        "....owwwoowwwwo.",
        "....ooooooooooo.",
    };
    private static readonly string[] ZSmall =
    {
        "oooo",
        "..o.",
        ".o..",
        "oooo",
    };
    private static readonly string[] ZBig =
    {
        "ooooo",
        "...o.",
        "..o..",
        ".o...",
        "ooooo",
    };
    // ------------------------------------------------------- jumping / reaching (side)

    // Leg stretched down-left (hind legs in the air), shade on the right like LegNear.
    private static readonly string[] LegBackDown =
    {
        ".....owwso",
        "....owwso.",
        "...owwso..",
        "..owwso...",
        ".owwso....",
        "owwso.....",
        "owwwo.....",
        ".ooo......",
    };
    // Leg stretched down-right (front legs reaching for the landing).
    private static readonly string[] LegFrontDown =
    {
        "owwso.....",
        ".owwso....",
        "..owwso...",
        "...owwso..",
        "....owwso.",
        ".....owwso",
        ".....owwwo",
        "......ooo.",
    };
    private static readonly string[] PawTuck = { "owwso", "owwwo", ".ooo." }; // paw peeking under a crouched body

    // Front paw stretched high up-right; the paw pad ends at the top row of the stamp.
    private static readonly string[] ArmUp = // x=17
    {
        "........oooo..",
        ".......owwwwo.",
        ".......owwwso.",
        ".......owwwso.",
        "........owwso.",
        "........owwso.",
        "........owwso.",
        ".......owwso..",
        ".......owwso..",
        ".......owwso..",
        "......owwso...",
        "......owwso...",
        "......owwso...",
        ".....owwso....",
        ".....owwso....",
        "....owwso.....",
        "...owwso......",
        "..owwso.......",
        ".owwso........",
        "owwso.........",
    };
    // Front paw stretched down-right over a ledge; the paw pad ends at the bottom row.
    private static readonly string[] ArmDown =
    {
        "owwso......",
        ".owwso.....",
        "..owwso....",
        "...owwso...",
        "....owwwso.",
        "....owwwwso",
        ".....owwwso",
        "......oooo.",
    };

    // ------------------------------------------------------- purr / eat / smug

    private static readonly string[] HeartBig = { "rr.rr", "rrrrr", ".rrr.", "..r.." };
    private static readonly string[] HeartSmall = { "r.r", "rrr", ".r." };
    private static readonly string[] HeartTiny = { "r" };
    private static readonly string[] PurrLine = { ".o...", "o.o.o", "...o." };

    private static readonly string[] Bowl = // x=20, y=27; row 31 is the ground line
    {
        "..kjkkjk..",
        "oooooooooo",
        "obllllllbo",
        ".obbbbbbo.",
        "..oooooo..",
    };

    private static readonly string[] EyesSmug = // heavy lids, glancing sideways
    {
        "wwww____wwww",
        "oooo____oooo",
        "gpeg____gpeg",
        "wggw____wggw",
    };
    private static readonly string[] MouthSmirk = // x=10, y=14: lopsided smile curling up on the right
    {
        "wwwoowwow",
        "wwwwwooww",
    };
    private static readonly string[] EarTiltRight = // x=18, y=3: right ear tipped outward
    {
        "xxxx...",
        "xxxx.o.",
        "xxooono",
        "onnnno.",
        "...no..",
    };
    private static readonly string[] TailTall = { ".oo..", "owwo.", "owwso", "owwso", "owwso", "owwso" }; // x=25, y=17
    private static readonly string[] TailTallFlick = { "...oo.", "ooowwo", "owwso.", "owwso.", "owwso.", "owwso." };
    private static readonly string[] SparkleSmall = { ".y.", "yhy", ".y." };
    private static readonly string[] SparkleBig = { "..y..", "..y..", "yyhyy", "..y..", "..y.." };

    // ------------------------------------------------------- play: sad / hug / nudge / tug

    private static readonly string[] EyesSad = // big wet puppy eyes looking up (x=8, y=9)
    {
        "gggg____gggg",
        "ghhg____ghhg",
        "gepg____gpeg",
        "wggw____wggw",
    };
    private static readonly string[] Tear = { "l", "l" };
    private static readonly string[] MouthFrown = { "wwooww", "wowwow" }; // x=11, y=14
    private static readonly string[] EarsFlat = // x=3, y=3: ears laid back like airplane wings
    {
        "xxxxxxxxxxxxxxxxxxxxxx",
        "xxxxxxxxxxxxxxxxxxxxxx",
        "xxxxxxxxxxxxxxxxxxxxxx",
        "oooo..............oooo",
        "onnnoooooooooooooonnno",
    };
    private static readonly string[] Pointer = // a tiny mouse pointer (the toy)
    {
        "p....",
        "pp...",
        "php..",
        "phhp.",
        "phhhp",
        "phpp.",
        "pp.p.",
    };
    private static readonly string[] PawBlob = { ".ooo.", "osswo", "ossso", ".ooo." };   // a shaded front paw, readable on her white chest
    private static readonly string[] EyesSqueeze = // >  < : squeezing with effort, still smiling
    {
        "owww____wwwo",
        "woow____woow",
        "owww____wwwo",
        "wwww____wwww",
    };
    private static readonly string[] PawGrip = { ".ooooooo", "owwwwsso", "osssssso", ".ooooooo" }; // a front paw stretched right, gripping
    private static readonly string[] SeekDot = { ".ooo.", "orrro", "orhro", "orrro", ".ooo." };   // a progress-bar dot (the toy), shiny
    private static readonly string[] Swish = { "o.", ".o", "o." };                                  // little motion marks

    private static string[] Shade(string[] rows) => rows.Select(r => r.Replace('w', 's')).ToArray();
    private static string[] FlipV(string[] rows) => rows.Reverse().ToArray();

    // ------------------------------------------------------------------ frames

    public sealed record AnimDef(string Name, int[] DurationsMs, List<uint[]> Frames)
    {
        public double Fps => 1000.0 * Frames.Count / DurationsMs.Sum();
    }

    public static List<AnimDef> BuildAll()
    {
        var list = new List<AnimDef>
        {
            new("Sit", new[] { 1700, 380, 1500, 150 }, new()
            {
                SitFrame().Pixels(),
                SitFrame(c => c.Stamp(TailFlick, 24, 20)).Pixels(),
                SitFrame().Pixels(),
                SitFrame(c => c.Stamp(EyesBlink, 8, 9)).Pixels(),
            }),
            new("Walk", new[] { 125, 125, 125, 125 }, new()
            {
                WalkFrame(0).Pixels(), WalkFrame(1).Pixels(), WalkFrame(2).Pixels(), WalkFrame(3).Pixels(),
            }),
            new("Sleep", new[] { 1100, 1100 }, new()
            {
                SleepFrame(false).Pixels(), SleepFrame(true).Pixels(),
            }),
            new("Think", new[] { 520, 520 }, new()
            {
                SitFrame(c => c.Stamp(ThinkDots2, 23, 0)).Pixels(),
                SitFrame(c => { c.Stamp(EarTwitchLeft, 4, 3); c.Stamp(ThinkDots3, 23, 0); }).Pixels(),
            }),
            new("Talk", new[] { 180, 180 }, new()
            {
                SitFrame().Pixels(),
                SitFrame(c => c.Stamp(MouthOpen, 11, 14)).Pixels(),
            }),
            new("Happy", new[] { 700, 1300 }, new()
            {
                SitFrame(c => { c.Stamp(EyesNarrow, 8, 9); c.Stamp(Blush, 8, 13); c.Stamp(Blush, 19, 13); }).Pixels(),
                SitFrame(c => { c.Stamp(EyesHappy, 8, 9); c.Stamp(Blush, 8, 13); c.Stamp(Blush, 19, 13); }).Pixels(),
            }),
            new("Confused", new[] { 420, 420 }, new()
            {
                ConfusedFrame(0).Pixels(), ConfusedFrame(1).Pixels(),
            }),
            new("Point", new[] { 380, 380 }, new()
            {
                PointFrame(0).Pixels(), PointFrame(1).Pixels(),
            }),
            new("Crouch", new[] { 120 }, new() { CrouchFrame().Pixels() }),
            new("Jump", new[] { 110, 110 }, new() { JumpFrame(0).Pixels(), JumpFrame(1).Pixels() }),
            new("Fall", new[] { 110, 110 }, new() { FallFrame(0).Pixels(), FallFrame(1).Pixels() }),
            new("ReachUp", new[] { 260, 260 }, new() { ReachUpFrame(0).Pixels(), ReachUpFrame(1).Pixels() }),
            new("ReachDown", new[] { 260, 260 }, new() { ReachDownFrame(0).Pixels(), ReachDownFrame(1).Pixels() }),
            new("Purr", new[] { 260, 260, 260, 260 }, new()
            {
                PurrFrame(0).Pixels(), PurrFrame(1).Pixels(), PurrFrame(2).Pixels(), PurrFrame(3).Pixels(),
            }),
            new("Eat", new[] { 240, 220, 240, 220 }, new()
            {
                EatFrame(0).Pixels(), EatFrame(1).Pixels(), EatFrame(2).Pixels(), EatFrame(3).Pixels(),
            }),
            new("Smug", new[] { 400, 400 }, new() { SmugFrame(0).Pixels(), SmugFrame(1).Pixels() }),
            new("Sad", new[] { 900, 900 }, new() { SadFrame(0).Pixels(), SadFrame(1).Pixels() }),
            new("Hug", new[] { 300, 300 }, new() { HugFrame(0).Pixels(), HugFrame(1).Pixels() }),
            new("Nudge", new[] { 220, 220 }, new() { NudgeFrame(0).Pixels(), NudgeFrame(1).Pixels() }),
            new("Tug", new[] { 200, 200 }, new() { TugFrame(0).Pixels(), TugFrame(1).Pixels() }),
            new("PullDot", new[] { 220, 220 }, new() { TugFrame(0, SeekDot).Pixels(), TugFrame(1, SeekDot).Pixels() }),
        };
        return list;
    }

    private static Canvas SitFrame(Action<Canvas>? patch = null)
    {
        var c = new Canvas();
        c.Stamp(Head, 0, 0);
        c.Stamp(SitBody, 0, 17);
        patch?.Invoke(c);
        return c;
    }

    private static Canvas WalkFrame(int f)
    {
        var c = new Canvas();
        int bob = (f % 2 == 1) ? -1 : 0;
        (int backNear, int backFar, int frontNear, int frontFar) = f switch
        {
            0 => (1, 7, 18, 13),
            2 => (7, 1, 13, 18),
            _ => (4, 5, 16, 15),
        };
        c.Stamp(LegFar, backFar, 25);
        c.Stamp(LegFar, frontFar, 25);
        c.Stamp(LegNear, backNear, 25);
        c.Stamp(LegNear, frontNear, 25);
        c.Stamp(f % 2 == 1 ? WalkTailFlick : WalkTail, 0, 6 + bob);
        c.Stamp(WalkBody, 0, 15 + bob);

        var head = new Canvas();
        head.Stamp(Head, 0, 0);
        head.Stamp(EyesLookRight, 8, 9);
        c.StampCanvas(head, 7, 5 + bob);
        return c;
    }

    private static Canvas SleepFrame(bool breathIn)
    {
        var c = new Canvas();
        c.Stamp(Loaf, 0, 18);
        if (breathIn) c.Stamp(LoafBreath, 0, 17);
        c.Stamp(SleepTail, 0, 27);

        var head = new Canvas();
        head.Stamp(Head, 0, 0);
        head.Stamp(EyesSleep, 8, 9);
        c.StampCanvas(head, -3, 12);
        c.Stamp(SleepPaws, 0, 29);

        if (breathIn)
        {
            c.Stamp(ZSmall, 21, 11);
            c.Stamp(ZBig, 25, 4);
        }
        else
        {
            c.Stamp(ZSmall, 22, 10);
        }
        return c;
    }

    private static Canvas ConfusedFrame(int f)
    {
        var body = new Canvas();
        body.Stamp(SitBody, 0, 17);
        // fill the chest top so the sheared head never leaves a hole
        body.Stamp(new[] { "........owwwwwwwwwso" }, 0, 16);

        var head = new Canvas();
        head.Stamp(Head, 0, 0);
        head.Stamp(EyesWide, 8, 9);
        // open the chin into the chest (like the sitting pose)
        head.Stamp(new[] { "wwwwwwww" }, 10, 17);
        var tilted = head.ShearY(13.5, -0.16);
        body.StampCanvas(tilted, 0, 0);

        body.Stamp(Question, 25, f == 0 ? 3 : 2);
        return body;
    }

    private static Canvas PointFrame(int f)
    {
        var c = SitFrame();
        c.Stamp(PawRaised, 18, 13 - f);
        return c;
    }

    private static Canvas SideHead(string[] eyes)
    {
        var head = new Canvas();
        head.Stamp(Head, 0, 0);
        head.Stamp(eyes, 8, 9);
        return head;
    }

    // Squashed walk pose: body and head 3-4 px lower, paws splayed and tucked.
    private static Canvas CrouchFrame()
    {
        var c = new Canvas();
        c.Stamp(Shade(PawTuck), 6, 29);
        c.Stamp(Shade(PawTuck), 14, 29);
        c.Stamp(PawTuck, 0, 29);
        c.Stamp(PawTuck, 19, 29);
        c.Stamp(WalkTail, 0, 8);
        c.Stamp(WalkBody, 0, 17);
        c.StampCanvas(SideHead(EyesLookRight), 7, 8);
        return c;
    }

    // Going up: body tilted up to the right, hind legs pushing down-back, front paws reaching up, tail trailing.
    private static Canvas JumpFrame(int f)
    {
        var c = new Canvas();
        c.Stamp(FlipV(f == 0 ? WalkTail : WalkTailFlick), 0, 17);
        c.Stamp(Shade(LegBackDown), 8, 21 + f);
        c.Stamp(LegBackDown, 4, 22 + f);
        var body = new Canvas();
        body.Stamp(WalkBody, 3, 11);
        c.StampCanvas(body.ShearY(14, -0.35), 0, 0);
        c.Stamp(Shade(PawRaised), 22, 15 - f);
        c.StampCanvas(SideHead(EyesLookRight), 7, 0);
        c.Stamp(PawRaised, 20, 17 - f);
        return c;
    }

    // Going down: legs spread for landing, ears and tail blown up.
    private static Canvas FallFrame(int f)
    {
        var c = new Canvas();
        c.Stamp(f == 0 ? WalkTail : WalkTailFlick, 2, 2);
        c.Stamp(Shade(LegBackDown), 4, 21);
        c.Stamp(Shade(LegFrontDown), 12, 21);
        c.Stamp(LegBackDown, 0, 22 + f);
        c.Stamp(LegFrontDown, 16, 22 + f);
        c.Stamp(WalkBody, 2, 12);
        c.StampCanvas(SideHead(EyesLookRight), 7, 2);
        // ear tips stretched up by the air
        c.Stamp(new[] { "o..............o" }, 13, 4 - f);
        c.Stamp(new[] { "o..............o" }, 13, 3);
        return c;
    }

    // Sitting tall, neck stretched, near paw tapping high up-right.
    private static Canvas ReachUpFrame(int f)
    {
        var c = new Canvas();
        c.Stamp(SitBody, 0, 17);
        c.Stamp(new[] { "........owwwwwwwwwso", "........owwwwwwwwwso" }, 0, 15);
        c.StampCanvas(SideHead(EyesLookRight), 0, -2);
        c.Stamp(new[] { ".......ooowwwwwwwwooo" }, 0, 15);
        c.Stamp(ArmUp, 17, 1 - f);
        return c;
    }

    // Standing on a ledge (row 31), leaning forward, near front paw pressing down at the bottom-right.
    private static readonly string[] EyesDownRight = // focused, looking down at what she presses
    {
        "wwww____wwww",
        "wggw____wggw",
        "ghep____ghep",
        "wgpp____wgpp",
    };
    // Her near front paw curled down to the right; the pad (bottom-right) is what touches the target.
    private static readonly string[] ArmTap =
    {
        "ooo.........",
        "wwwo........",
        "wwwwo.......",
        "owwwwo......",
        ".owwwso.....",
        "..owwwso....",
        "...owwwso...",
        "....owwwsoo.",
        ".....owwwwwo",
        ".....onwnwso",
        "......ooooo.",
    };

    // Sitting upright on the ledge, eyes on her paw, tapping down at the bottom-right (typing / pressing).
    private static Canvas ReachDownFrame(int f) => SitFrame(c =>
    {
        c.Stamp(Enumerable.Repeat("xxxxxxx", 11).ToArray(), 23, 20);   // tail tucked so the paw reads
        c.Stamp(EyesDownRight, 8, 9);
        c.Stamp(Blush, 8, 13); c.Stamp(Blush, 19, 13);
        c.Stamp(ArmTap, 18, f == 0 ? 21 : 19);   // frame 1: paw lifted between taps
    });

    // Being petted: eyes closed, blush, head bobbing, hearts floating up and shrinking.
    private static Canvas PurrFrame(int f)
    {
        var c = new Canvas();
        c.Stamp(SitBody, 0, 17);
        int bob = f % 2;
        var head = SideHead(EyesHappy);
        head.Stamp(Blush, 8, 13);
        head.Stamp(Blush, 19, 13);
        c.StampCanvas(head, 0, bob);
        c.Stamp(new[] { ".......ooowwwwwwwwooo" }, 0, 17 + bob); // reopen the chin into the chest
        c.Stamp(PurrLine, 0, bob == 0 ? 22 : 25);
        Heart(c, 23, f);
        Heart(c, 27, f + 2);
        return c;
    }

    private static void Heart(Canvas c, int x, int phase)
    {
        switch (phase % 4)
        {
            case 0: c.Stamp(HeartBig, x, 9); break;
            case 1: c.Stamp(HeartBig, x, 7); break;
            case 2: c.Stamp(HeartSmall, x + 1, 5); break;
            default: c.Stamp(HeartTiny, x + 2, 3); break;
        }
    }

    // Crouched side pose, head lowered into a bowl at the bottom-right; head dips on odd frames.
    private static Canvas EatFrame(int f)
    {
        var c = new Canvas();
        c.Stamp(WalkTail, 0, 8);
        c.Stamp(WalkBody, 0, 17);
        bool dip = f % 2 == 1;
        var head = SideHead(dip ? EyesHappy : EyesNarrow);
        if (f == 2) head.Stamp(MouthOpen, 11, 14);
        c.StampCanvas(head, 8, dip ? 13 : 12);
        c.Stamp(Shade(PawTuck), 5, 29);
        c.Stamp(PawTuck, 0, 29);
        c.Stamp(PawTuck, 13, 29);
        c.Stamp(Bowl, 20, 27);
        if (dip) c.Stamp(new[] { ".x" }, 22, 27); // a kibble was eaten
        if (f == 2) c.Stamp(new[] { "k..", "..j" }, 17, 25); // crumbs flying
        return c;
    }

    // Teasing grin: heavy-lidded eyes, smirk, one ear tilted, tail up with a flick and a sparkle.
    // Sitting small: ears flat, big teary eyes, little frown; the tear rolls down.
    private static Canvas SadFrame(int f) => SitFrame(c =>
    {
        c.Stamp(EarsFlat, 3, 3);
        c.Stamp(EyesSad, 8, 9);
        c.Stamp(MouthFrown, 11, 14);
        c.Stamp(Tear, 9, f == 0 ? 13 : 14);
    });

    // Paws wrapped around the pointer, eyes shut, blushing; a heart pops up on the squeeze.
    private static Canvas HugFrame(int f) => SitFrame(c =>
    {
        c.Stamp(EyesHappy, 8, 9);
        c.Stamp(Blush, 8, 13); c.Stamp(Blush, 19, 13);
        int y = f == 0 ? 19 : 18;                 // squeeze: pointer and paws lift a pixel
        c.Stamp(Pointer, 12, y);
        c.Stamp(PawBlob, 8, y + 3);
        c.Stamp(PawBlob, 16, y + 3);
        if (f == 0) c.Stamp(HeartSmall, 23, 4); else c.Stamp(HeartBig, 23, 2);
    });

    // Rubbing her head on it: the head tips sideways and back, happy and blushing.
    private static Canvas NudgeFrame(int f)
    {
        var c = new Canvas();
        c.Stamp(SitBody, 0, 17);
        c.Stamp(new[] { "........owwwwwwwwwso" }, 0, 16);   // fill the chest so the tipped head leaves no hole
        var head = SideHead(EyesHappy);
        head.Stamp(Blush, 8, 13); head.Stamp(Blush, 19, 13);
        head.Stamp(new[] { "wwwwwwww" }, 10, 17);
        c.StampCanvas(head.ShearY(13.5, f == 0 ? 0.12 : 0.2), f, 0);
        c.Stamp(Swish, 25, 9 + f);
        return c;
    }

    // Leaning back, both paws gripping the pointer to her right, determined > < eyes.
    private static Canvas TugFrame(int f, string[]? toy = null)
    {
        var c = new Canvas();
        c.Stamp(SitBody, 0, 17);
        c.Stamp(new[] { "........owwwwwwwwwso" }, 0, 16);
        var head = SideHead(EyesSqueeze);
        head.Stamp(Blush, 8, 13); head.Stamp(Blush, 19, 13);
        head.Stamp(new[] { "wwwwwwww" }, 10, 17);
        c.StampCanvas(head, -1 - f, 0);
        c.Stamp(Enumerable.Repeat("xxxxxxxx", 9).ToArray(), 22, 20);   // tail tucked away so the grip reads
        if (toy == null) c.Stamp(Pointer, 26 - f, 15);
        c.Stamp(PawGrip, 19 - f, 17);
        c.Stamp(PawGrip, 19 - f, 20);
        if (toy != null) c.Stamp(toy, 25 - f, 18);   // held in front of her paws
        return c;
    }

    private static Canvas SmugFrame(int f) => SitFrame(c =>
    {
        c.Stamp(EyesSmug, 8, 9);
        c.Stamp(MouthSmirk, 10, 14);
        c.Stamp(EarTiltRight, 18, 3);
        c.Stamp(f == 0 ? TailTall : TailTallFlick, 25, 17);
        if (f == 0) c.Stamp(SparkleSmall, 26, 4);
        else c.Stamp(SparkleBig, 26, 2);
    });

    // ------------------------------------------------------------------ canvas

    internal sealed class Canvas
    {
        private readonly char[,] _px = new char[Size, Size];

        public Canvas()
        {
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                    _px[x, y] = '.';
        }

        public char this[int x, int y]
        {
            get => (x < 0 || y < 0 || x >= Size || y >= Size) ? '.' : _px[x, y];
            set { if (x >= 0 && y >= 0 && x < Size && y < Size) _px[x, y] = value; }
        }

        public void Stamp(string[] rows, int dx, int dy)
        {
            for (int r = 0; r < rows.Length; r++)
            {
                var row = rows[r];
                for (int i = 0; i < row.Length; i++)
                {
                    char ch = row[i];
                    if (ch is '.' or '_' or ' ') continue;
                    this[dx + i, dy + r] = ch == 'x' ? '.' : ch;
                }
            }
        }

        public void StampCanvas(Canvas other, int dx, int dy)
        {
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    char ch = other._px[x, y];
                    if (ch != '.') this[x + dx, y + dy] = ch;
                }
        }

        /// <summary>Vertical shear around column cx: column x moves by round((x-cx)*k) rows (a cheap pixel "tilt").</summary>
        public Canvas ShearY(double cx, double k)
        {
            var res = new Canvas();
            for (int x = 0; x < Size; x++)
            {
                int off = (int)Math.Round((x - cx) * k, MidpointRounding.AwayFromZero);
                for (int y = 0; y < Size; y++)
                {
                    char ch = _px[x, y];
                    if (ch != '.') res[x, y + off] = ch;
                }
            }
            return res;
        }

        public uint[] Pixels()
        {
            var p = new uint[Size * Size];
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                    p[y * Size + x] = ColorOf(_px[x, y]);
            return p;
        }
    }
}
