namespace Gpixel.App;

/// <summary>작은 아이콘(24×24 격자 SVG 경로). 선 아이콘은 Lucide 모양(ISC 라이선스), <see cref="VectorIcon.Discord"/>는 Simple Icons의 채운 로고(CC0).</summary>
public enum VectorIcon
{
    Eye,
    EyeOff,
    Lock,
    LockOpen,
    Copy,
    Plus,
    Minus,
    Refresh,
    Layers,
    FilePlus,
    FolderPlus,
    Play,
    Pause,
    Dice,
    ExternalLink,
    /// <summary>두 화살표 맞바꾸기(⇄).</summary>
    Swap,
    /// <summary>양쪽 화살표 하나(↔).</summary>
    MoveHorizontal,
    /// <summary>위아래 화살표 하나(↕).</summary>
    MoveVertical,
    Check,
    X,
    /// <summary>작은 점(채워 그리면 ●).</summary>
    Dot,
    Circle,
    /// <summary>가운데 점이 있는 원(라디오 단추 켜짐).</summary>
    CircleDot,
    Square,
    Diamond,
    Triangle,
    /// <summary>끌기 손잡이(점 2×3).</summary>
    Grip,
    ChevronDown,
    ChevronRight,
    ChevronLeft,
    ChevronUp,
    Menu,
    Star,
    Heart,
    Download,
    Trash,
    /// <summary>연필(고치기).</summary>
    Pencil,
    ArrowUpLeft,
    ArrowUp,
    ArrowUpRight,
    ArrowLeft,
    ArrowRight,
    ArrowDownLeft,
    ArrowDown,
    ArrowDownRight,
    /// <summary>아래로 꺾여 오른쪽으로(↳).</summary>
    CornerDownRight,
    /// <summary>아래로 꺾여 왼쪽으로(↵ 줄바꿈).</summary>
    CornerDownLeft,
    /// <summary>둥근 바탕 위에 올리는 굵은 느낌표(경고 표시 안쪽).</summary>
    ExclamationMark,
    /// <summary>둥근 바탕 위에 올리는 물음표.</summary>
    QuestionMark,
    /// <summary>둥근 바탕·네모 테 안에 올리는 "i".</summary>
    InfoMark,
    /// <summary>그림이 든 책(AI 참고 자료).</summary>
    BookImage,
    /// <summary>열린 폴더(폴더 열기).</summary>
    FolderOpen,
    /// <summary>그림(사진 틀 + 산).</summary>
    Image,
    /// <summary>반짝임(AI로 하기).</summary>
    Sparkles,
    /// <summary>Discord 로고(채운 도형, Simple Icons — 제보·아이디어 단추).</summary>
    Discord,
    /// <summary>지구본(홈페이지 — Lucide globe).</summary>
    Globe,
    /// <summary>커피 잔(개발자에게 커피 사주기 — Lucide coffee).</summary>
    Coffee,
    /// <summary>3×3 칸 격자(AI 그림 픽셀로 정리).</summary>
    Grid3x3,
    /// <summary>필름(GIF로 저장).</summary>
    Film,
    /// <summary>아래 화살표가 붙은 그림(그림 파일로 저장).</summary>
    ImageDown,
    /// <summary>대각선 양쪽 화살표(크게).</summary>
    Maximize2,
    /// <summary>가로 슬라이더 세 줄(설정).</summary>
    SlidersHorizontal,
    /// <summary>왼쪽으로 돌아가는 화살표(되돌리기).</summary>
    Undo2,
    /// <summary>원 안의 느낌표(경고 — Lucide circle-alert).</summary>
    CircleAlert,
    /// <summary>사슬 고리(비율 묶기 — Lucide link).</summary>
    Link,
    /// <summary>세로로 둘로 나뉜 사각형(좌우로 나눠 열기 — Lucide columns-2).</summary>
    Columns2,
    /// <summary>원 안의 "i"(설명 — Lucide info).</summary>
    Info,
    /// <summary>가로로 긴 타원(도형 도구 — 직접 그린 경로).</summary>
    Oval,
    /// <summary>위가 뾰족한 이등변 삼각형(도형 도구 — 직접 그린 경로).</summary>
    TriangleTall,
    /// <summary>정오각형(도형 도구 정다각형 — 직접 그린 경로).</summary>
    Pentagon,
    /// <summary>안에서 밖으로 감기는 나선(도형 도구 달팽이 — 직접 그린 경로).</summary>
    Spiral,
    /// <summary>원기둥(도형 도구 — 직접 그린 경로).</summary>
    Cylinder,
    /// <summary>등각 정육면체(도형 도구 주사위 — 직접 그린 경로).</summary>
    Cube,
    /// <summary>대각선 직선(도형 도구 선 — 직접 그린 경로).</summary>
    LineDiagonal,
    /// <summary>위로 휜 곡선(도형 도구 곡선 — 직접 그린 경로).</summary>
    Curve,
    /// <summary>열린 원호(계산 중 — Lucide loader-circle, 돌려 그린다).</summary>
    LoaderCircle,
    /// <summary>상자(에셋 패키지 — Lucide package).</summary>
    Package,
    /// <summary>위 화살표와 받침(내보내기 — Lucide upload).</summary>
    Upload,
    /// <summary>꺾쇠 두 개(스크립트 효과 — Lucide code).</summary>
    Code,
}

public static class VectorIcons
{
    private static readonly Dictionary<VectorIcon, Geometry[]> Cache = new();
    private static readonly Dictionary<(IBrush, double), IPen> Pens = new();

    /// <summary>원 하나의 경로(반원 두 개).</summary>
    private static string Circle(double cx, double cy, double r)
        => FormattableString.Invariant($"M{cx - r} {cy}a{r} {r} 0 1 0 {2 * r} 0a{r} {r} 0 1 0 {-2 * r} 0z");

    private static string[] Paths(VectorIcon icon) => icon switch
    {
        VectorIcon.Eye =>
        [
            "M2.062 12.348a1 1 0 0 1 0-.696 10.75 10.75 0 0 1 19.876 0 1 1 0 0 1 0 .696 10.75 10.75 0 0 1-19.876 0",
            "M9 12a3 3 0 1 0 6 0a3 3 0 1 0-6 0",
        ],
        VectorIcon.EyeOff =>
        [
            "M10.733 5.076a10.744 10.744 0 0 1 11.205 6.575 1 1 0 0 1 0 .696 10.747 10.747 0 0 1-1.444 2.49",
            "M14.084 14.158a3 3 0 0 1-4.242-4.242",
            "M17.479 17.499a10.75 10.75 0 0 1-15.417-5.151 1 1 0 0 1 0-.696 10.75 10.75 0 0 1 4.446-5.143",
            "M2 2L22 22",
        ],
        VectorIcon.Lock =>
        [
            "M5 11h14a2 2 0 0 1 2 2v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-7a2 2 0 0 1 2-2z",
            "M7 11V7a5 5 0 0 1 10 0v4",
        ],
        VectorIcon.LockOpen =>
        [
            "M5 11h14a2 2 0 0 1 2 2v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-7a2 2 0 0 1 2-2z",
            "M7 11V7a5 5 0 0 1 9.9-1",
        ],
        VectorIcon.Copy =>
        [
            "M10 8h10a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H10a2 2 0 0 1-2-2V10a2 2 0 0 1 2-2z",
            "M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2",
        ],
        VectorIcon.Plus => ["M5 12h14", "M12 5v14"],
        VectorIcon.Minus => ["M5 12h14"],
        VectorIcon.Refresh =>
        [
            "M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8",
            "M21 3v5h-5",
            "M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16",
            "M8 16H3v5",
        ],
        VectorIcon.Layers =>
        [
            "M12.83 2.18a2 2 0 0 0-1.66 0L2.6 6.08a1 1 0 0 0 0 1.83l8.58 3.91a2 2 0 0 0 1.66 0l8.58-3.9a1 1 0 0 0 0-1.83z",
            "M2 12a1 1 0 0 0 .58.91l8.6 3.91a2 2 0 0 0 1.65 0l8.58-3.9A1 1 0 0 0 22 12",
            "M2 17a1 1 0 0 0 .58.91l8.6 3.91a2 2 0 0 0 1.65 0l8.58-3.9A1 1 0 0 0 22 17",
        ],
        VectorIcon.FilePlus =>
        [
            "M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7Z",
            "M14 2v4a2 2 0 0 0 2 2h4",
            "M9 15h6",
            "M12 18v-6",
        ],
        VectorIcon.FolderPlus =>
        [
            "M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z",
            "M12 10v6",
            "M9 13h6",
        ],
        VectorIcon.Play => ["M6 3l14 9-14 9z"],
        VectorIcon.Pause =>
        [
            "M7 4h2a1 1 0 0 1 1 1v14a1 1 0 0 1-1 1H7a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1z",
            "M15 4h2a1 1 0 0 1 1 1v14a1 1 0 0 1-1 1h-2a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1z",
        ],
        VectorIcon.Dice =>
        [
            "M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z",
            "M16 8h.01", "M8 8h.01", "M8 16h.01", "M16 16h.01", "M12 12h.01",
        ],
        VectorIcon.ExternalLink => ["M15 3h6v6", "M10 14 21 3", "M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6"],
        VectorIcon.Swap => ["M8 3 4 7l4 4", "M4 7h16", "m16 21 4-4-4-4", "M20 17H4"],
        VectorIcon.MoveHorizontal => ["m18 8 4 4-4 4", "M2 12h20", "m6 8-4 4 4 4"],
        VectorIcon.MoveVertical => ["M12 2v20", "m8 18 4 4 4-4", "m8 6 4-4 4 4"],
        VectorIcon.Check => ["M20 6 9 17l-5-5"],
        VectorIcon.X => ["M18 6 6 18", "m6 6 12 12"],
        VectorIcon.Dot => [Circle(12, 12, 4)],
        VectorIcon.Circle => [Circle(12, 12, 10)],
        VectorIcon.CircleDot => [Circle(12, 12, 10), Circle(12, 12, 3)],
        VectorIcon.Square => ["M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z"],
        VectorIcon.Diamond =>
        [
            "M2.7 10.3a2.41 2.41 0 0 0 0 3.41l7.59 7.59a2.41 2.41 0 0 0 3.41 0l7.59-7.59a2.41 2.41 0 0 0 0-3.41l-7.59-7.59a2.41 2.41 0 0 0-3.41 0Z",
        ],
        VectorIcon.Triangle => ["M13.73 4a2 2 0 0 0-3.46 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3Z"],
        VectorIcon.Grip => [Circle(9, 5, 1), Circle(9, 12, 1), Circle(9, 19, 1), Circle(15, 5, 1), Circle(15, 12, 1), Circle(15, 19, 1)],
        VectorIcon.ChevronDown => ["m6 9 6 6 6-6"],
        VectorIcon.ChevronRight => ["m9 18 6-6-6-6"],
        VectorIcon.ChevronLeft => ["m15 18-6-6 6-6"],
        VectorIcon.ChevronUp => ["m18 15-6-6-6 6"],
        VectorIcon.Menu => ["M4 6h16", "M4 12h16", "M4 18h16"],
        VectorIcon.Star => ["M12 2l3.09 6.26L22 9.27l-5 4.87 1.18 6.88L12 17.77l-6.18 3.25L7 14.14 2 9.27l6.91-1.01z"],
        VectorIcon.Heart =>
        [
            "M19 14c1.49-1.46 3-3.21 3-5.5A5.5 5.5 0 0 0 16.5 3c-1.76 0-3 .5-4.5 2-1.5-1.5-2.74-2-4.5-2A5.5 5.5 0 0 0 2 8.5c0 2.3 1.5 4.05 3 5.5l7 7Z",
        ],
        VectorIcon.Download => ["M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4", "m7 10 5 5 5-5", "M12 15V3"],
        VectorIcon.Trash =>
        [
            "M3 6h18",
            "M19 6v14c0 1-1 2-2 2H7c-1 0-2-1-2-2V6",
            "M8 6V4c0-1 1-2 2-2h4c1 0 2 1 2 2v2",
            "M10 11v6",
            "M14 11v6",
        ],
        VectorIcon.Pencil =>
        [
            "M21.174 6.812a1 1 0 0 0-3.986-3.987L3.842 16.174a2 2 0 0 0-.5.83l-1.321 4.352a.5.5 0 0 0 .623.622l4.353-1.32a2 2 0 0 0 .83-.497z",
            "m15 5 4 4",
        ],
        VectorIcon.ArrowUpLeft => ["M7 17V7h10", "M17 17 7 7"],
        VectorIcon.ArrowUp => ["m5 12 7-7 7 7", "M12 19V5"],
        VectorIcon.ArrowUpRight => ["M7 7h10v10", "M7 17 17 7"],
        VectorIcon.ArrowLeft => ["m12 19-7-7 7-7", "M19 12H5"],
        VectorIcon.ArrowRight => ["M5 12h14", "m12 5 7 7-7 7"],
        VectorIcon.ArrowDownLeft => ["M17 7 7 17", "M17 17H7V7"],
        VectorIcon.ArrowDown => ["M12 5v14", "m19 12-7 7-7-7"],
        VectorIcon.ArrowDownRight => ["m7 7 10 10", "M17 7v10H7"],
        VectorIcon.CornerDownRight => ["m15 10 5 5-5 5", "M4 4v7a4 4 0 0 0 4 4h12"],
        VectorIcon.CornerDownLeft => ["m9 10-5 5 5 5", "M20 4v7a4 4 0 0 1-4 4H4"],
        VectorIcon.ExclamationMark => ["M12 4v10", "M12 19.5h.01"],
        VectorIcon.QuestionMark => ["M8 8.5a4 4 0 1 1 5.5 3.7c-.9.4-1.5 1.2-1.5 2.2v.6", "M12 19.5h.01"],
        VectorIcon.InfoMark => ["M12 10.5v9", "M12 4.5h.01"],
        VectorIcon.BookImage =>
        [
            "m20 13.7-2.1-2.1a2 2 0 0 0-2.8 0L9.7 17",
            "M4 19.5v-15A2.5 2.5 0 0 1 6.5 2H19a1 1 0 0 1 1 1v18a1 1 0 0 1-1 1H6.5a1 1 0 0 1 0-5H20",
            Circle(10, 8, 2),
        ],
        VectorIcon.FolderOpen =>
            ["m6 14 1.5-2.9A2 2 0 0 1 9.24 10H20a2 2 0 0 1 1.94 2.5l-1.54 6a2 2 0 0 1-1.95 1.5H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h3.9a2 2 0 0 1 1.69.9l.81 1.2a2 2 0 0 0 1.67.9H18a2 2 0 0 1 2 2v2"],
        VectorIcon.Image =>
        [
            "M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z",
            Circle(9, 9, 2),
            "m21 15-3.086-3.086a2 2 0 0 0-2.828 0L6 21",
        ],
        VectorIcon.Sparkles =>
        [
            "M9.937 15.5A2 2 0 0 0 8.5 14.063l-6.135-1.582a.5.5 0 0 1 0-.962L8.5 9.936A2 2 0 0 0 9.937 8.5l1.582-6.135a.5.5 0 0 1 .963 0L14.063 8.5A2 2 0 0 0 15.5 9.937l6.135 1.581a.5.5 0 0 1 0 .964L15.5 14.063a2 2 0 0 0-1.437 1.437l-1.582 6.135a.5.5 0 0 1-.963 0z",
            "M20 3v4", "M22 5h-4", "M4 17v2", "M5 18H3",
        ],
        VectorIcon.Discord =>
        [
            "M20.317 4.3698a19.7913 19.7913 0 00-4.8851-1.5152.0741.0741 0 00-.0785.0371c-.211.3753-.4447.8648-.6083 1.2495-1.8447-.2762-3.68-.2762-5.4868 0-.1636-.3933-.4058-.8742-.6177-1.2495a.077.077 0 00-.0785-.037 19.7363 19.7363 0 00-4.8852 1.515.0699.0699 0 00-.0321.0277C.5334 9.0458-.319 13.5799.0992 18.0578a.0824.0824 0 00.0312.0561c2.0528 1.5076 4.0413 2.4228 5.9929 3.0294a.0777.0777 0 00.0842-.0276c.4616-.6304.8731-1.2952 1.226-1.9942a.076.076 0 00-.0416-.1057c-.6528-.2476-1.2743-.5495-1.8722-.8923a.077.077 0 01-.0076-.1277c.1258-.0943.2517-.1923.3718-.2914a.0743.0743 0 01.0776-.0105c3.9278 1.7933 8.18 1.7933 12.0614 0a.0739.0739 0 01.0785.0095c.1202.099.246.1981.3728.2924a.077.077 0 01-.0066.1276 12.2986 12.2986 0 01-1.873.8914.0766.0766 0 00-.0407.1067c.3604.698.7719 1.3628 1.225 1.9932a.076.076 0 00.0842.0286c1.961-.6067 3.9495-1.5219 6.0023-3.0294a.077.077 0 00.0313-.0552c.5004-5.177-.8382-9.6739-3.5485-13.6604a.061.061 0 00-.0312-.0286zM8.02 15.3312c-1.1825 0-2.1569-1.0857-2.1569-2.419 0-1.3332.9555-2.4189 2.157-2.4189 1.2108 0 2.1757 1.0952 2.1568 2.419 0 1.3332-.9555 2.4189-2.1569 2.4189zm7.9748 0c-1.1825 0-2.1569-1.0857-2.1569-2.419 0-1.3332.9554-2.4189 2.1569-2.4189 1.2108 0 2.1757 1.0952 2.1568 2.419 0 1.3332-.946 2.4189-2.1568 2.4189Z",
        ],
        VectorIcon.Globe => [Circle(12, 12, 10), "M12 2a14.5 14.5 0 0 0 0 20 14.5 14.5 0 0 0 0-20", "M2 12h20"],
        VectorIcon.Coffee =>
        [
            "M10 2v2",
            "M14 2v2",
            "M16 8a1 1 0 0 1 1 1v8a4 4 0 0 1-4 4H7a4 4 0 0 1-4-4V9a1 1 0 0 1 1-1h14a4 4 0 1 1 0 8h-1",
            "M6 2v2",
        ],
        VectorIcon.Grid3x3 =>
        [
            "M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z",
            "M3 9h18", "M3 15h18", "M9 3v18", "M15 3v18",
        ],
        VectorIcon.Film =>
        [
            "M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z",
            "M7 3v18", "M3 7.5h4", "M3 12h18", "M3 16.5h4", "M17 3v18", "M17 7.5h4", "M17 16.5h4",
        ],
        VectorIcon.ImageDown =>
        [
            "M10.3 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2v10l-3.1-3.1a2 2 0 0 0-2.814.014L6 21",
            "m14 19 3 3v-5.5",
            "m17 22 3-3",
            Circle(9, 9, 2),
        ],
        VectorIcon.Maximize2 => ["M15 3h6v6", "m21 3-7 7", "m3 21 7-7", "M9 21H3v-6"],
        VectorIcon.SlidersHorizontal =>
        [
            "M21 4h-7", "M10 4H3", "M21 12h-9", "M8 12H3", "M21 20h-5", "M12 20H3",
            "M14 2v4", "M8 10v4", "M16 18v4",
        ],
        VectorIcon.Undo2 => ["M9 14 4 9l5-5", "M4 9h10.5a5.5 5.5 0 0 1 5.5 5.5a5.5 5.5 0 0 1-5.5 5.5H11"],
        VectorIcon.CircleAlert => [Circle(12, 12, 10), "M12 8v4", "M12 16h.01"],
        VectorIcon.Link => ["M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71", "M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"],
        VectorIcon.Columns2 => ["M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z", "M12 3v18"],
        VectorIcon.Info => [Circle(12, 12, 10), "M12 16v-4", "M12 8h.01"],
        VectorIcon.Oval => ["M2 12a10 7 0 1 0 20 0a10 7 0 1 0-20 0z"],
        VectorIcon.TriangleTall => ["M12 2 19 22H5Z"],
        VectorIcon.Pentagon => ["M12 2.5 21.51 9.41 17.88 20.59H6.12L2.49 9.41Z"],
        VectorIcon.Spiral => ["M10.5 12a1.5 1.5 0 0 1 3 0a3 3 0 0 1-6 0a4.5 4.5 0 0 1 9 0a6 6 0 0 1-12 0a7.5 7.5 0 0 1 15 0"],
        VectorIcon.Cylinder => ["M4 6a8 3 0 1 0 16 0a8 3 0 1 0-16 0z", "M4 6v12a8 3 0 0 0 16 0V6"],
        VectorIcon.Cube => ["M12 2 21 7v10l-9 5-9-5V7Z", "m3 7 9 5 9-5", "M12 12v10"],
        VectorIcon.LineDiagonal => ["M4 20 20 4"],
        VectorIcon.Curve => ["M3 19Q12 1 21 19"],
        VectorIcon.LoaderCircle => ["M21 12a9 9 0 1 1-6.219-8.56"],
        VectorIcon.Package =>
        [
            "M11 21.73a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73z",
            "M12 22V12",
            "m3.3 7 7.703 4.734a2 2 0 0 0 1.994 0L20.7 7",
            "m7.5 4.27 9 5.15",
        ],
        VectorIcon.Upload => ["M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4", "m17 8-5-5-5 5", "M12 3v12"],
        VectorIcon.Code => ["m16 18 6-6-6-6", "m8 6-6 6 6 6"],
        _ => [],
    };

    private static Geometry[] Geometries(VectorIcon icon)
    {
        if (Cache.TryGetValue(icon, out var g)) return g;
        g = Paths(icon).Select(p => (Geometry)StreamGeometry.Parse(p)).ToArray();
        Cache[icon] = g;
        return g;
    }

    /// <summary>선 없이 채우기만 하는 로고 아이콘(선 굵기를 더하면 모양이 뭉개진다).</summary>
    private static bool Solid(VectorIcon icon) => icon is VectorIcon.Discord;

    /// <summary>
    /// <paramref name="area"/> 가운데에 <paramref name="size"/> 크기로 아이콘을 그린다.
    /// <paramref name="filled"/>면 닫힌 도형 안을 같은 색으로 채운다(●·◆·★처럼). 로고 아이콘은 늘 채우기만 한다.
    /// </summary>
    public static void Draw(DrawingContext g, VectorIcon icon, Rect area, double size, IBrush brush, double strokeWidth = 2, bool filled = false)
    {
        double scale = size / 24.0;
        double x = Math.Round(area.X + (area.Width - size) / 2), y = Math.Round(area.Y + (area.Height - size) / 2);
        using (g.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(x, y)))
        {
            bool solid = Solid(icon);
            var pen = solid ? null : Pen(brush, strokeWidth);
            foreach (var geo in Geometries(icon))
                g.DrawGeometry(filled || solid ? brush : null, pen, geo);
        }
    }

    /// <summary>둥근 끝 펜. 바뀌지 않는 붓만 모아 둔다(테마·전환이 만드는 붓은 매번 새로).</summary>
    private static IPen Pen(IBrush brush, double strokeWidth)
    {
        if (brush is not IImmutableBrush) return new Pen(brush, strokeWidth) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        if (!Pens.TryGetValue((brush, strokeWidth), out var pen))
            Pens[(brush, strokeWidth)] = pen = new Pen(brush, strokeWidth) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }.ToImmutable();
        return pen;
    }
}

/// <summary>
/// 선 아이콘 하나를 크기에 맞춰 그리는 컨트롤. 붓을 주지 않으면 글자색(<see cref="Avalonia.Controls.Documents.TextElement.ForegroundProperty"/>)을
/// 물려받아, 단추 안에서는 단추 글자처럼 꺼짐·올림 색을 따른다.
/// </summary>
public class IconView : Control
{
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        Avalonia.Controls.Documents.TextElement.ForegroundProperty.AddOwner<IconView>();

    public static readonly StyledProperty<VectorIcon> IconProperty =
        AvaloniaProperty.Register<IconView, VectorIcon>(nameof(Icon));

    static IconView() => AffectsRender<IconView>(ForegroundProperty, IconProperty);

    private readonly double _strokeWidth;

    public IconView(VectorIcon icon, IBrush? brush = null, double strokeWidth = 2.2)
    {
        Icon = icon;
        if (brush is not null) Foreground = brush;
        _strokeWidth = strokeWidth;
    }

    public VectorIcon Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>닫힌 도형 안을 채운다(●·◆·★ 모양).</summary>
    public bool Filled { get; init; }

    public override void Render(DrawingContext g)
    {
        // 투명 바탕: 글자처럼 칸 전체가 마우스(풍선 도움말)를 받게
        g.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        VectorIcons.Draw(g, Icon, new Rect(Bounds.Size), Math.Min(Bounds.Width, Bounds.Height), Foreground ?? AppTheme.TextBrush, _strokeWidth, Filled);
    }
}
