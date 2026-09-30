import { useState, useEffect, useRef } from "react";
import type { ComponentType, SVGProps } from "react";
import { motion, AnimatePresence } from "motion/react";
import {
  ShieldCheck,
  ShieldOff,
  Zap,
  Power,
  Activity,
  Globe,
  Lock,
  FileCode2,
  ChevronRight,
  RefreshCw,
  Minimize2,
  Square,
  X,
  Server,
  Wifi,
  WifiOff,
  Terminal,
  CheckCircle2,
  AlertTriangle,
  Clock,
  ArrowUpRight,
  ExternalLink,
  Download,
  MonitorDot,
  Layers,
  PlugZap,
  Github,
  ChevronDown,
  Check,
  LayoutDashboard,
  Settings2,
  Gamepad2,
  Pencil,
  Save,
  Sparkles,
  Search,
  Star,
  Play,
  SlidersHorizontal,
  Cloud,
  Smartphone,
} from "lucide-react";

// ─── Types ────────────────────────────────────────────────────────────────────

type Tab = "dashboard" | "profiles" | "youtube" | "telegram" | "warp" | "gamefilter" | "settings";

type WarpProbeState = "available" | "restricted" | "partial" | "serviceError" | "unavailable" | "timedOut" | "routeUnconfirmed" | "availabilityUnknown" | "regionRestricted";
type WarpEndpointDiagnostic = { label: string; state: WarpProbeState; detail: string; httpStatus?: number; latencyMs?: number };
type WarpScenarioDiagnostic = { state: WarpProbeState; summary: string; endpoints: Record<string, WarpEndpointDiagnostic> };
type WarpPerformanceEndpoint = { label: string; available: boolean; medianLatencyMs?: number; jitterMs?: number; detail: string };
type WarpPerformance = { warpConnected: boolean; checkedAt: string; summary: string; verdict?: string; recommendation?: string; endpoints: Record<string, WarpPerformanceEndpoint> };
type WarpRouteProfile = { id: string; name: string; kind: "site" | "game"; builtInId?: string; enabled: boolean; connection: "auto" | "direct" | "warpProxy" | "warpSystem"; domains: string[]; ipRanges: string[]; executablePath?: string };

type WarpInfo = {
  state: "notInstalled" | "disconnected" | "connecting" | "connected" | "error";
  installed: boolean;
  connected: boolean;
  pending: boolean;
  detail: string;
  error?: string;
  version?: string;
  cliPath?: string;
  checkedAt?: string;
  progress?: number;
  stage?: string;
  registered?: boolean;
  serviceRunning?: boolean;
  protocol?: string;
  routingMode?: "smart" | "full" | "proxy" | "dnsOnly";
  effectiveRoutingMode?: string;
  protocolPreference?: "auto" | "masque" | "wireguard";
  masquePreference?: "h3-with-h2-fallback" | "h3-only" | "h2-only";
  dnsMode?: "auto" | "udp" | "doh" | "dot" | "system";
  proxyPort?: number;
  profiles?: WarpRouteProfile[];
  optimization?: { selectedProtocol: string; reason: string; checkedAt: string; routeDecisions?: Record<string, string> };
  routingConflicts?: string[];
  routingWarnings?: string[];
  bootstrapActive?: boolean;
  telegramCompatibilityActive?: boolean;
  scenarios: { telegram: boolean; games: boolean };
  performance?: WarpPerformance;
  diagnostics?: {
    routeActive: boolean;
    dnsAvailable: boolean;
    latencyMs?: number;
    externalIp?: string;
    coloCode?: string;
    traceLocationCode?: string;
    pop?: { code: string; city: string; country: string; status?: string; displayName: string };
    detail: string;
    checkedAt: string;
    scenarios?: Record<string, string>;
    scenarioDetails?: Record<string, WarpScenarioDiagnostic>;
  };
};

type NativeWebView = { postMessage: (message: unknown) => void };
type NativeWindow = Window & {
  chrome?: { webview?: NativeWebView };
  __vortexSettings?: {
    autostart?: boolean;
    tray?: boolean;
    selectedDnsProvider?: "auto" | "google" | "cloudflare" | "quad9";
    startProtectionOnStartup?: boolean;
    startupStrategy?: "auto" | "light" | "moderate" | "medium" | "complex" | "aggressive";
    gameFilterIpsetMode?: "any" | "loaded" | "none";
  };
  __vortexRuntime?: Record<string, any>;
};

function postNativeMessage(type: string, value?: boolean, serviceId?: string) {
  (window as NativeWindow).chrome?.webview?.postMessage({ type, value, serviceId });
}

function postNativePayload(payload: Record<string, unknown>) {
  (window as NativeWindow).chrome?.webview?.postMessage(payload);
}

function nativeSetting<K extends keyof NonNullable<NativeWindow["__vortexSettings"]>>(
  name: K,
  fallback: NonNullable<NativeWindow["__vortexSettings"]>[K]
) {
  return ((window as NativeWindow).__vortexSettings?.[name] ?? fallback) as NonNullable<NativeWindow["__vortexSettings"]>[K];
}

function useNativeMessage(handler: (message: any) => void) {
  const handlerRef = useRef(handler);
  handlerRef.current = handler;
  useEffect(() => {
    const webview = (window as NativeWindow).chrome?.webview as any;
    if (!webview?.addEventListener) return;
    const listener = (event: any) => handlerRef.current(event.data);
    webview.addEventListener("message", listener);
    return () => webview.removeEventListener("message", listener);
  }, []);
}

interface LogEntry {
  ts: string;
  level: "info" | "warn" | "error" | "ok";
  msg: string;
}

interface Metrics {
  uptimeSeconds: number;
  trafficBytes: number;
  packets: number;
  youtubePing: number | null;
  discordPing: number | null;
  telegramPing: number | null;
}

interface UpdateInfo {
  state: "idle" | "checking" | "current" | "available" | "availablePrompt" | "preparing" | "deferred" | "installing" | "unavailable" | "failed" | "downloading";
  currentVersion?: string;
  availableVersion?: string;
  checkedAt?: string;
  error?: string;
}

interface HealthItem { state: string; detail: string; latencyMs: number | null; measurementType?: "routeprobe" | "voiceprobe" | "gamesession"; stages?: Record<string, string | null> }
type HealthMap = Record<string, HealthItem>;
interface HostsInfo { applied: boolean; entryCount: number; updatedAt?: string; detail: string; sourceCommit?: string; telegramFallbackCount?: number; telegramDnsCount?: number; robloxCdnCount?: number }
interface GameFilterInfo { selectedGames: string[]; protocol: "udp" | "tcp" | "all"; ipsetMode?: "any" | "loaded" | "none" }
interface DomainListsInfo { bypass: string[]; exclusions: string[] }
interface SettingsInfo {
  autostart: boolean;
  tray: boolean;
  selectedDnsProvider?: "auto" | "google" | "cloudflare" | "quad9";
  startProtectionOnStartup?: boolean;
  startupStrategy?: "auto" | "light" | "moderate" | "medium" | "complex" | "aggressive";
  gameFilterIpsetMode?: "any" | "loaded" | "none";
}
interface FavoriteProfile { level: string; tuning: number; mode?: "standard" | "mobile" }
interface CompatibilityInfo {
  mode: "standard" | "mobile";
  networkKey?: string;
  remembered?: boolean;
  youtubeTransport?: "auto" | "quic" | "http2";
  discordVoiceMode?: "auto" | "direct" | "minimal" | "off";
  discordVoiceApplied?: "direct" | "minimal" | "off";
  providerName?: string;
  telegramRouteCount?: number;
  optimizeState?: "idle" | "cached" | "checking" | "confirmed" | "fallback" | "partial" | "manual";
  optimizeDetail?: string;
  dnsState?: "idle" | "starting" | "ready" | "restored" | "portBusy" | "dohUnavailable" | "error" | "restoreError";
  dnsDetail?: string;
  dnsResolver?: string;
  dnsAdapter?: string;
  winwsDomainCount?: number;
  pinnedIpCount?: number;
  pinnedIpDetail?: string;
  routes?: Record<string, { strategy?: string; route?: string; state?: string; detail?: string; checkedAt?: string }>;
}
interface AdvisorCandidate { level: string; tuning: number; mode?: "standard" | "mobile"; label: string; healthy: number; total: number; latencyMs: number | null; summary: string; score?: number; source?: string }
interface AdvisorInfo {
  state: "idle" | "detecting" | "testing" | "complete" | "failed";
  provider?: string;
  level?: string;
  tuning?: number;
  progress?: number;
  total?: number;
  message?: string;
  best?: string;
  error?: string;
  candidates?: AdvisorCandidate[];
}

const PROFILE_LEVELS = [
  { id: "light", label: "Лёгкий", short: "Минимальное вмешательство", description: "Узкая обработка трафика с минимальной нагрузкой." },
  { id: "moderate", label: "Умеренный", short: "Базовая защита", description: "Базовые TLS, QUIC и UDP-правила для обычных блокировок." },
  { id: "medium", label: "Средний", short: "Устойчивый профиль", description: "Сбалансированная стратегия для большинства провайдеров." },
  { id: "complex", label: "Сложный", short: "Усиленный TLS/QUIC", description: "Multisplit и fake-правила для более строгого DPI." },
  { id: "aggressive", label: "Агрессивный", short: "Широкий fallback", description: "Максимально широкий профиль для жёсткой фильтрации." },
] as const;

const PROFILE_TUNINGS = [
  { id: 0, label: "Стандарт", detail: "штатные повторы" },
  { id: 1, label: "Усиленный +2", detail: "+2 повтора" },
  { id: 2, label: "Усиленный +4", detail: "+4 повтора" },
  { id: 3, label: "Максимум +6", detail: "+6 повторов" },
] as const;

const MOBILE_LEVEL_NAMES: Record<string, string> = {
  light: "Мобильный быстрый",
  moderate: "Мобильный связной",
  medium: "Мобильный стабильный",
  complex: "Мобильный усиленный",
  aggressive: "Мобильный резервный",
};

function createProfileCatalog(mode: "standard" | "mobile") { return PROFILE_LEVELS.flatMap((level) =>
  PROFILE_TUNINGS.map((tuning) => ({
    level: level.id,
    tuning: tuning.id,
    mode,
    label: `${mode === "mobile" ? MOBILE_LEVEL_NAMES[level.id] : level.label} · ${tuning.label}`,
    levelLabel: mode === "mobile" ? MOBILE_LEVEL_NAMES[level.id] : level.label,
    tuningLabel: tuning.label,
    short: mode === "mobile" ? "Узкий профиль мобильного оператора" : level.short,
    description: mode === "mobile" ? `Отдельные правила Telegram WebSocket и Discord для мобильной сети. ${tuning.detail}.` : `${level.description} ${tuning.detail}.`,
  })),
); }

const PROFILE_CATALOG = createProfileCatalog("standard");

function profileKey(profile: FavoriteProfile) {
  return `${profile.mode ?? "standard"}:${profile.level}:${profile.tuning}`;
}

function profileCatalogItem(profile: FavoriteProfile) {
  const catalog = createProfileCatalog(profile.mode ?? "standard");
  return catalog.find((item) => item.level === profile.level && item.tuning === profile.tuning) ?? catalog[8];
}

type BrandIconProps = SVGProps<SVGSVGElement> & { size?: number };
type BrandIcon = ComponentType<BrandIconProps>;

function YouTubeIcon({ size = 18, ...props }: BrandIconProps) {
  return <svg width={size} height={size} viewBox="0 0 24 24" fill="none" aria-hidden="true" {...props}>
    <path fill="currentColor" d="M23 12.04c0-2.31-.27-3.96-.55-4.91a3.18 3.18 0 0 0-2.22-2.2C19.25 4.65 17.58 4.4 12 4.4s-7.25.25-8.23.53a3.18 3.18 0 0 0-2.22 2.2C1.27 8.08 1 9.73 1 12.04s.27 3.96.55 4.91a3.18 3.18 0 0 0 2.22 2.2c.98.28 2.65.53 8.23.53s7.25-.25 8.23-.53a3.18 3.18 0 0 0 2.22-2.2c.28-.95.55-2.6.55-4.91Z" />
    <path fill="#fff" d="m10.1 15.55 5.18-3.51-5.18-3.5v7.01Z" />
  </svg>;
}

function DiscordIcon({ size = 18, ...props }: BrandIconProps) {
  return <svg width={size} height={size} viewBox="0 0 24 24" fill="none" aria-hidden="true" {...props}>
    <path fill="currentColor" d="M19.54 5.63A16.9 16.9 0 0 0 15.45 4.4l-.5 1.02a15.5 15.5 0 0 0-5.9 0l-.5-1.02a16.8 16.8 0 0 0-4.1 1.24C1.86 9.54 1.15 13.34 1.5 17.08a16.5 16.5 0 0 0 5.02 2.51l1.22-1.66a9.6 9.6 0 0 1-1.9-.91l.46-.36c3.66 1.72 7.64 1.72 11.26 0l.47.36c-.61.36-1.25.67-1.91.91l1.22 1.66a16.45 16.45 0 0 0 5.03-2.51c.41-4.34-.7-8.1-2.84-11.45ZM8.72 14.78c-1.1 0-2-1-2-2.25s.88-2.25 2-2.25c1.12 0 2.02 1 2 2.25 0 1.25-.88 2.25-2 2.25Zm6.56 0c-1.1 0-2-1-2-2.25s.88-2.25 2-2.25c1.12 0 2.02 1 2 2.25 0 1.25-.88 2.25-2 2.25Z" />
  </svg>;
}

function TelegramIcon({ size = 18, ...props }: BrandIconProps) {
  return <svg width={size} height={size} viewBox="0 0 24 24" fill="none" aria-hidden="true" {...props}>
    <path fill="currentColor" d="M21.46 3.38c.3-.12.62.15.53.47l-3.24 15.3c-.12.54-.73.75-1.14.4l-4.9-4.11-2.38 2.3c-.26.26-.72.08-.73-.3l-.16-3.65 8.88-8.03c.17-.16-.06-.41-.25-.28L7.1 12.35l-3.7-1.22c-.5-.16-.54-.84-.06-1.07L21.46 3.38Z" />
  </svg>;
}

function GameFilterIcon({ size = 18, ...props }: BrandIconProps) {
  return <svg width={size} height={size} viewBox="0 0 24 24" fill="none" aria-hidden="true" {...props}>
    <path d="M8.2 8.1h7.6c2.2 0 3.9 1.5 4.3 3.6l.7 4.1c.3 1.8-1.7 3-3.1 1.9l-2.1-1.7H8.4l-2.1 1.7c-1.4 1.1-3.4-.1-3.1-1.9l.7-4.1c.4-2.1 2.1-3.6 4.3-3.6Z" fill="currentColor" opacity=".95" />
    <path d="M7.2 11.1v3.4M5.5 12.8h3.4M15.8 12.2h.1M18.1 14.1h.1" stroke="#fff" strokeWidth="1.4" strokeLinecap="round" />
  </svg>;
}

// ─── Helpers ──────────────────────────────────────────────────────────────────

function nowTs() {
  return new Date().toTimeString().slice(0, 8);
}

function formatDuration(totalSeconds: number) {
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = Math.floor(totalSeconds % 60);
  return [hours, minutes, seconds].map((value) => String(value).padStart(2, "0")).join(":");
}

function formatBytes(bytes: number) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 ** 2) return `${(bytes / 1024).toFixed(1)} KB`;
  if (bytes < 1024 ** 3) return `${(bytes / 1024 ** 2).toFixed(1)} MB`;
  return `${(bytes / 1024 ** 3).toFixed(2)} GB`;
}

function formatCount(value: number) {
  return new Intl.NumberFormat("ru-RU", { notation: value >= 10000 ? "compact" : "standard", maximumFractionDigits: 1 }).format(value);
}

// ─── Micro Components ─────────────────────────────────────────────────────────

function Dot({ on, pulse = false }: { on: boolean; pulse?: boolean }) {
  return (
    <span className="relative inline-flex items-center justify-center w-2.5 h-2.5">
      {on && pulse && (
        <span
          className="absolute inline-flex h-full w-full rounded-full animate-ping opacity-60"
          style={{ background: "#00D68F" }}
        />
      )}
      <span
        className="relative inline-flex rounded-full w-2 h-2 transition-colors duration-300"
        style={{ background: on ? "#00D68F" : "#2A2A44" }}
      />
    </span>
  );
}

function Toggle({
  checked,
  onChange,
  size = "md",
  disabled = false,
}: {
  checked: boolean;
  onChange: (v: boolean) => void;
  size?: "sm" | "md";
  disabled?: boolean;
}) {
  const w = size === "sm" ? 34 : 42;
  const h = size === "sm" ? 20 : 24;
  const thumb = size === "sm" ? 14 : 18;
  const travel = w - thumb - 6;

  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      disabled={disabled}
      onClick={() => onChange(!checked)}
      className="relative block flex-shrink-0 rounded-full transition-all duration-200 focus:outline-none focus-visible:ring-2 focus-visible:ring-[#6C5CE7]/60 cursor-pointer overflow-hidden"
      style={{
        width: w,
        height: h,
        background: checked
          ? "linear-gradient(135deg,#6C5CE7,#0078D4)"
          : "rgba(255,255,255,0.1)",
        opacity: disabled ? 0.55 : 1,
        cursor: disabled ? "wait" : "pointer",
      }}
    >
      <motion.span
        className="absolute rounded-full bg-white shadow pointer-events-none"
        style={{ width: thumb, height: thumb, left: 3, top: 3 }}
        animate={{ x: checked ? travel : 0 }}
        transition={{ type: "spring", stiffness: 500, damping: 32 }}
      />
    </button>
  );
}

function GlassCard({
  children,
  className = "",
  style = {},
  glow = false,
}: {
  children: React.ReactNode;
  className?: string;
  style?: React.CSSProperties;
  glow?: boolean;
}) {
  return (
    <div
      className={`relative rounded-2xl transition-all duration-200 ${className}`}
      style={{
        background: "rgba(255,255,255,0.038)",
        backdropFilter: "blur(24px)",
        WebkitBackdropFilter: "blur(24px)",
        border: glow
          ? "1px solid rgba(108,92,231,0.45)"
          : "1px solid rgba(255,255,255,0.075)",
        boxShadow: glow
          ? "0 0 0 3px rgba(108,92,231,0.1), 0 8px 32px rgba(0,0,0,0.3)"
          : "0 2px 12px rgba(0,0,0,0.25)",
        ...style,
      }}
    >
      {children}
    </div>
  );
}

function SectionLabel({ children }: { children: React.ReactNode }) {
  return (
    <p className="text-[10px] font-mono uppercase tracking-[0.14em] text-[#3A3A5E] mb-3">
      {children}
    </p>
  );
}

function Divider() {
  return (
    <div
      className="h-px my-3.5"
      style={{ background: "rgba(255,255,255,0.055)" }}
    />
  );
}

function CheckBox({
  checked,
  onChange,
  label,
  sub,
}: {
  checked: boolean;
  onChange: (v: boolean) => void;
  label: string;
  sub?: string;
}) {
  return (
    <button
      type="button"
      role="checkbox"
      aria-checked={checked}
      onClick={() => onChange(!checked)}
      className="flex items-start gap-3 text-left w-full group rounded-lg focus:outline-none focus-visible:ring-2 focus-visible:ring-[#6C5CE7]/60"
    >
      <span
        className="flex-shrink-0 w-4 h-4 mt-0.5 rounded flex items-center justify-center transition-all duration-150"
        style={{
          background: checked ? "#6C5CE7" : "rgba(255,255,255,0.06)",
          border: `1px solid ${checked ? "#6C5CE7" : "rgba(255,255,255,0.12)"}`,
        }}
      >
        {checked && <Check size={10} className="text-white" strokeWidth={3} />}
      </span>
      <div>
        <span
          className="text-sm font-medium text-white block"
          style={{ fontFamily: "'Plus Jakarta Sans', sans-serif" }}
        >
          {label}
        </span>
        {sub && (
          <span className="text-[11px] text-[#3A3A5E] block mt-0.5">{sub}</span>
        )}
      </div>
    </button>
  );
}

// ─── Custom Select ─────────────────────────────────────────────────────────────

function Select({
  value,
  onChange,
  options,
}: {
  value: string;
  onChange: (v: string) => void;
  options: { value: string; label: string; sub?: string }[];
}) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    function handleClickOutside(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) {
        setOpen(false);
      }
    }
    document.addEventListener("mousedown", handleClickOutside);
    return () => document.removeEventListener("mousedown", handleClickOutside);
  }, []);

  const selected = options.find((o) => o.value === value)!;

  return (
    <div ref={ref} className={`relative ${open ? "z-[120]" : "z-10"}`}>
      <button
        onClick={() => setOpen(!open)}
        className="w-full flex items-center justify-between gap-2 px-3.5 py-2.5 rounded-xl text-sm transition-all duration-150"
        style={{
          background: open ? "rgba(108,92,231,0.12)" : "rgba(255,255,255,0.05)",
          border: `1px solid ${open ? "rgba(108,92,231,0.4)" : "rgba(255,255,255,0.09)"}`,
          color: "#EEEEF8",
          fontFamily: "'Plus Jakarta Sans', sans-serif",
        }}
      >
        <div className="text-left">
          <div className="text-sm font-medium">{selected.label}</div>
          {selected.sub && (
            <div className="text-[10px] font-mono text-[#4A4A6A] mt-0.5">
              {selected.sub}
            </div>
          )}
        </div>
        <motion.span
          animate={{ rotate: open ? 180 : 0 }}
          transition={{ duration: 0.18 }}
        >
          <ChevronDown size={14} className="text-[#4A4A6A] flex-shrink-0" />
        </motion.span>
      </button>

      <AnimatePresence>
        {open && (
          <motion.div
            initial={{ opacity: 0, y: -6, scale: 0.98 }}
            animate={{ opacity: 1, y: 0, scale: 1 }}
            exit={{ opacity: 0, y: -6, scale: 0.98 }}
            transition={{ duration: 0.15 }}
            className="absolute z-[130] w-full mt-1.5 rounded-xl overflow-hidden"
            style={{
              background: "#12121C",
              border: "1px solid rgba(255,255,255,0.1)",
              boxShadow: "0 12px 40px rgba(0,0,0,0.5)",
            }}
          >
            {options.map((opt) => (
              <button
                key={opt.value}
                onClick={() => {
                  onChange(opt.value);
                  setOpen(false);
                }}
                className="w-full flex items-center justify-between gap-2 px-3.5 py-2.5 text-left transition-all duration-100"
                style={{
                  background:
                    opt.value === value
                      ? "rgba(108,92,231,0.15)"
                      : "transparent",
                  color: opt.value === value ? "#B5AEFC" : "#8080A0",
                }}
                onMouseEnter={(e) => {
                  if (opt.value !== value)
                    (e.currentTarget as HTMLButtonElement).style.background =
                      "rgba(255,255,255,0.05)";
                }}
                onMouseLeave={(e) => {
                  if (opt.value !== value)
                    (e.currentTarget as HTMLButtonElement).style.background =
                      "transparent";
                }}
              >
                <div>
                  <div
                    className="text-sm font-medium"
                    style={{
                      fontFamily: "'Plus Jakarta Sans', sans-serif",
                      color: opt.value === value ? "#B5AEFC" : "#EEEEF8",
                    }}
                  >
                    {opt.label}
                  </div>
                  {opt.sub && (
                    <div className="text-[10px] font-mono text-[#3A3A5A] mt-0.5">
                      {opt.sub}
                    </div>
                  )}
                </div>
                {opt.value === value && (
                  <Check size={13} className="text-[#6C5CE7] flex-shrink-0" />
                )}
              </button>
            ))}
          </motion.div>
        )}
      </AnimatePresence>
    </div>
  );
}

// ─── Log Console ──────────────────────────────────────────────────────────────

const LOG_COLOR = {
  info: "#5A7FFF",
  warn: "#FFB340",
  error: "#FF4D6D",
  ok: "#00D68F",
};

const LOG_TAG = { info: "INFO", warn: "WARN", error: "ERR ", ok: " OK " };

function LogConsole({ logs }: { logs: LogEntry[] }) {
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (ref.current) ref.current.scrollTop = ref.current.scrollHeight;
  }, [logs]);

  return (
    <GlassCard>
      <div
        className="flex items-center gap-2 px-4 py-2.5 border-b"
        style={{ borderColor: "rgba(255,255,255,0.065)" }}
      >
        <Terminal size={12} className="text-[#3A3A5E]" />
        <span className="text-[10px] font-mono uppercase tracking-widest text-[#3A3A5E]">
          Системный журнал
        </span>
        <div className="ml-auto flex items-center gap-1.5">
          <Dot on pulse />
          <span className="text-[10px] font-mono text-[#00D68F]">live</span>
        </div>
      </div>
      <div
        ref={ref}
        className="h-28 overflow-y-auto px-4 py-3 space-y-1"
        style={{ scrollbarWidth: "none" }}
      >
        {logs.map((l, i) => (
          <div
            key={i}
            className="flex items-start gap-2 font-mono text-[11px] leading-relaxed"
          >
            <span className="text-[#1E1E38] flex-shrink-0 tabular-nums">
              {l.ts}
            </span>
            <span
              className="flex-shrink-0 px-1 rounded text-[9px] font-bold"
              style={{
                background: `${LOG_COLOR[l.level]}18`,
                color: LOG_COLOR[l.level],
              }}
            >
              {LOG_TAG[l.level]}
            </span>
            <span
              style={{
                color: l.level === "error" ? "#FF4D6D" : "#5A5A7A",
              }}
            >
              {l.msg}
            </span>
          </div>
        ))}
      </div>
    </GlassCard>
  );
}

// ═══════════════════════════════════════════════════════════════════════════════
// TAB 1 — DASHBOARD
// ═══════════════════════════════════════════════════════════════════════════════

function Dashboard({
  protection,
  pending,
  onToggleProtection,
  ytActive,
  dcActive,
  tgActive,
  ytSelected,
  dcSelected,
  tgSelected,
  webSelected,
  gameFilterSelected,
  bypassLevel,
  bypassTuning,
  compatibilityMode,
  favoriteProfiles,
  health,
  hosts,
  advisor,
  onRunAdvisor,
  onStopAdvisor,
  onAdvisorCandidateSelected,
  onProfileSelected,
  onFavoriteToggle,
  onOpenProfiles,
  onServiceSelected,
  onRestoreHosts,
  logs,
  metrics,
}: {
  protection: boolean;
  pending: boolean;
  onToggleProtection: () => void;
  ytActive: boolean;
  dcActive: boolean;
  tgActive: boolean;
  ytSelected: boolean;
  dcSelected: boolean;
  tgSelected: boolean;
  webSelected: boolean;
  gameFilterSelected: boolean;
  bypassLevel: string;
  bypassTuning: number;
  compatibilityMode: "standard" | "mobile";
  favoriteProfiles: FavoriteProfile[];
  health: HealthMap;
  hosts: HostsInfo;
  advisor: AdvisorInfo;
  onRunAdvisor: () => void;
  onStopAdvisor: () => void;
  onAdvisorCandidateSelected: (candidate: AdvisorCandidate) => void;
  onProfileSelected: (profile: FavoriteProfile) => void;
  onFavoriteToggle: (profile: FavoriteProfile, favorite: boolean) => void;
  onOpenProfiles: () => void;
  onServiceSelected: (serviceId: string, selected: boolean) => void;
  onRestoreHosts: () => void;
  logs: LogEntry[];
  metrics: Metrics;
}) {
  const statusCards = [
    {
      id: "yt",
      label: "YouTube",
      Icon: YouTubeIcon,
      active: ["healthy", "partial"].includes(health.youtube?.state ?? ""),
      iconColor: "#FF4D6D",
      ping: health.youtube?.latencyMs != null ? `Маршрут ${health.youtube.latencyMs} ms` : "—",
      sub: health.youtube?.detail ?? (ytActive ? "Проверяем сеть" : "Не выбран"),
      subColor: health.youtube?.state === "healthy" ? "#00D68F" : "#FFB340",
    },
    {
      id: "dc",
      label: "Discord",
      Icon: DiscordIcon,
      active: ["healthy", "partial"].includes(health.discord?.state ?? ""),
      iconColor: "#5865F2",
      ping: health.discord?.latencyMs != null ? `${health.discord.measurementType === "voiceprobe" ? "Voice" : health.discord.measurementType === "gamesession" ? "Игра" : "Маршрут"} ${health.discord.latencyMs} ms` : "—",
      sub: health.discord?.detail ?? (dcActive ? "Проверяем сеть" : "Не выбран"),
      subColor: health.discord?.state === "healthy" ? "#00D68F" : health.discord?.state === "partial" ? "#FFB340" : "#FF4D6D",
    },
    {
      id: "tg",
      label: "Telegram",
      Icon: TelegramIcon,
      active: ["healthy", "partial"].includes(health.telegram?.state ?? ""),
      iconColor: "#29B6F6",
      ping: health.telegram?.latencyMs != null ? `Маршрут ${health.telegram.latencyMs} ms` : "—",
      sub: health.telegram?.detail ?? (tgActive ? "Проверяем сеть" : "Не выбран"),
      subColor: health.telegram?.state === "healthy" ? "#00D68F" : health.telegram?.state === "partial" ? "#FFB340" : "#FF4D6D",
    },
  ];

  return (
    <div className="flex flex-col gap-5">
      {/* ── Central power button ── */}
      <GlassCard
        className="p-8 flex flex-col items-center justify-center relative overflow-hidden"
        glow={protection}
      >
        {/* Ambient glow behind button */}
        <AnimatePresence>
          {protection && (
            <motion.div
              className="absolute inset-0 pointer-events-none"
              initial={{ opacity: 0 }}
              animate={{ opacity: 1 }}
              exit={{ opacity: 0 }}
              transition={{ duration: 0.5 }}
              style={{
                background:
                  "radial-gradient(ellipse 60% 70% at 50% 50%, rgba(108,92,231,0.14) 0%, transparent 75%)",
              }}
            />
          )}
        </AnimatePresence>

        {/* Pulsing ring */}
        <div className="relative mb-6">
          {protection && (
            <>
              <motion.div
                className="absolute inset-0 rounded-full"
                animate={{ scale: [1, 1.35], opacity: [0.35, 0] }}
                transition={{ repeat: Infinity, duration: 2, ease: "easeOut" }}
                style={{
                  background:
                    "radial-gradient(circle, rgba(108,92,231,0.5) 0%, transparent 70%)",
                }}
              />
              <motion.div
                className="absolute inset-0 rounded-full"
                animate={{ scale: [1, 1.6], opacity: [0.2, 0] }}
                transition={{
                  repeat: Infinity,
                  duration: 2,
                  ease: "easeOut",
                  delay: 0.6,
                }}
                style={{
                  background:
                    "radial-gradient(circle, rgba(0,120,212,0.4) 0%, transparent 70%)",
                }}
              />
            </>
          )}

          {/* Button */}
          <motion.button
            whileHover={pending && !protection ? {} : { scale: 1.04 }}
            whileTap={pending && !protection ? {} : { scale: 0.95 }}
            onClick={onToggleProtection}
            disabled={pending && !protection}
            className="relative w-24 h-24 rounded-full flex items-center justify-center transition-all duration-300 focus:outline-none"
            style={{
              background: protection
                ? "linear-gradient(135deg, #6C5CE7 0%, #0078D4 100%)"
                : "rgba(255,255,255,0.06)",
              border: protection
                ? "1.5px solid rgba(255,255,255,0.2)"
                : "1.5px solid rgba(255,255,255,0.1)",
              boxShadow: protection
                ? "0 0 0 4px rgba(108,92,231,0.18), 0 8px 32px rgba(108,92,231,0.45)"
                : "0 4px 20px rgba(0,0,0,0.3)",
            }}
          >
            <Power
              size={32}
              className="transition-colors duration-300"
              style={{ color: protection ? "#fff" : "#3A3A5E" }}
              strokeWidth={1.8}
            />
          </motion.button>
        </div>

        {/* Label */}
        <div className="text-center relative z-10">
          <motion.div
            key={protection ? "on" : "off"}
            initial={{ opacity: 0, y: 4 }}
            animate={{ opacity: 1, y: 0 }}
            transition={{ duration: 0.2 }}
          >
            <h2
              className="text-xl font-bold"
              style={{
                fontFamily: "'Plus Jakarta Sans', sans-serif",
                color: protection ? "#fff" : "#4A4A6A",
              }}
            >
               {pending ? (protection ? "Остановить защиту" : "Запуск защиты…") : protection ? "Защита активна" : "Запустить защиту"}
            </h2>
            <p className="text-[12px] font-mono mt-1" style={{ color: protection ? "#8080C0" : "#2A2A44" }}>
              {pending
                ? (protection ? "Нажмите, чтобы прервать проверку и остановить" : "Проверяем файлы и запускаем winws")
                : protection
                ? "winws · WinDivert — работают"
                : "Нажмите, чтобы включить все службы"}
            </p>
          </motion.div>
        </div>

        {/* Status row */}
        {protection && (
          <motion.div
            initial={{ opacity: 0, y: 8 }}
            animate={{ opacity: 1, y: 0 }}
            transition={{ delay: 0.15 }}
            className="flex items-center gap-4 mt-5"
          >
            {[
              { label: "Uptime", value: formatDuration(metrics.uptimeSeconds) },
              { label: "Трафик", value: formatBytes(metrics.trafficBytes) },
              { label: "Пакеты", value: formatCount(metrics.packets) },
            ].map((s) => (
              <div key={s.label} className="text-center">
                <div className="text-[11px] font-mono text-[#3A3A5E]">{s.label}</div>
                <div
                  className="text-sm font-bold text-white mt-0.5"
                  style={{ fontFamily: "'Plus Jakarta Sans', sans-serif" }}
                >
                  {s.value}
                </div>
              </div>
            ))}
          </motion.div>
        )}
      </GlassCard>

      <GlassCard className="p-5 overflow-hidden relative">
        <div className="absolute inset-0 pointer-events-none" style={{ background: "radial-gradient(circle at 92% 0%, rgba(108,92,231,0.12), transparent 42%)" }} />
        <div className="relative flex items-start justify-between gap-5">
          <div className="flex items-start gap-3 min-w-0">
            <div className="w-10 h-10 rounded-xl flex items-center justify-center flex-shrink-0"
              style={{ background: "linear-gradient(135deg, rgba(108,92,231,0.2), rgba(0,120,212,0.12))", border: "1px solid rgba(108,92,231,0.28)" }}>
              <Sparkles size={18} className="text-[#A99BFF]" />
            </div>
            <div>
              <div className="text-[13px] font-semibold text-[#EEEEF8]">Автоматический подбор профиля</div>
              <div className="text-[10px] leading-relaxed text-[#5A5A7A] mt-1 max-w-[650px]">
                {compatibilityMode === "mobile"
                  ? "Быстро сравнит две отдельные мобильные стратегии для Telegram, Discord и YouTube и сразу оставит лучшую."
                  : "Определит провайдера и проверит внутренние стратегии на выбранных сервисах. Пять понятных уровней на дашборде останутся без изменений."}
              </div>
              <div className="text-[9px] font-mono text-[#3A3A5E] mt-1.5">{compatibilityMode === "mobile" ? "Обычно занимает до 10–12 секунд; вспомогательный тайм-аут не перезапускает защиту." : "Проверка может занять несколько минут. Выполняется один HTTPS-запрос к ipapi.co; публичный IP в VortexPass не сохраняется."}</div>
            </div>
          </div>
          {advisor.state === "detecting" || advisor.state === "testing" ? (
            <motion.button whileHover={{ scale: 1.02 }} whileTap={{ scale: 0.98 }}
              type="button" onClick={onStopAdvisor}
              className="px-4 py-2.5 rounded-xl text-[11px] font-semibold whitespace-nowrap"
              style={{ color: "#FF9AAA", background: "rgba(255,77,109,0.12)", border: "1px solid rgba(255,77,109,0.3)" }}>
              Стоп
            </motion.button>
          ) : (
            <motion.button whileHover={{ scale: 1.02 }} whileTap={{ scale: 0.98 }}
              type="button" onClick={onRunAdvisor} disabled={pending}
              className="px-4 py-2.5 rounded-xl text-[11px] font-semibold whitespace-nowrap disabled:opacity-50"
              style={{ color: "#C2BCFF", background: "rgba(108,92,231,0.14)", border: "1px solid rgba(108,92,231,0.32)" }}>
              Подобрать профиль
            </motion.button>
          )}
        </div>

        {(advisor.state === "detecting" || advisor.state === "testing") && (
          <motion.div initial={{ opacity: 0, y: 4 }} animate={{ opacity: 1, y: 0 }} className="relative mt-4">
            <div className="flex justify-between text-[10px] font-mono mb-2">
              <span className="text-[#7777A8]">{advisor.message ?? "Проверяем сеть…"}</span>
              <span className="text-[#4A4A6A]">
                {advisor.state === "testing" ? `${Math.min((advisor.progress ?? 0) + 1, advisor.total ?? (compatibilityMode === "mobile" ? 2 : 20))}/${advisor.total ?? (compatibilityMode === "mobile" ? 2 : 20)}` : "DNS/provider"}
                {advisor.provider ? ` · ${advisor.provider}` : ""}
              </span>
            </div>
            <div className="h-1.5 rounded-full overflow-hidden bg-white/[0.04]">
              <motion.div className="h-full rounded-full" animate={{ width: `${advisor.state === "detecting" ? 5 : Math.max(5, (((advisor.progress ?? 0) + 1) / (advisor.total ?? (compatibilityMode === "mobile" ? 2 : 20))) * 100)}%` }}
                style={{ background: "linear-gradient(90deg, #6C5CE7, #0078D4)" }} />
            </div>
            {advisor.best && (
              <div className="text-[9px] font-mono text-[#5F5F88] mt-2">
                Лучший сейчас: <span className="text-[#A99BFF]">{advisor.best}</span>
              </div>
            )}
          </motion.div>
        )}

        {advisor.state === "failed" && <div className="relative mt-4 text-[10px] font-mono text-[#FF7A8F]">{advisor.error}</div>}

        {advisor.state === "complete" && (
          <motion.div initial={{ opacity: 0, y: 6 }} animate={{ opacity: 1, y: 0 }} className="relative mt-4">
            <div className="flex items-center justify-between mb-2">
              <span className="text-[10px] font-mono text-[#7474A0]">Лучшие варианты{advisor.provider ? ` · ${advisor.provider}` : ""}</span>
              <span className="text-[9px] font-mono text-[#3A3A5E]">{compatibilityMode === "mobile" ? "Лучший уже применён" : "Применение только вручную"}</span>
            </div>
            <div className="grid grid-cols-3 gap-2">
              {(advisor.candidates ?? []).map((candidate, index) => {
                const favorite = favoriteProfiles.some((profile) => profileKey(profile) === profileKey(candidate));
                const favoriteLimitReached = !favorite && favoriteProfiles.length >= 5;
                return (
                  <motion.div key={`${candidate.level}-${candidate.tuning}`} whileHover={{ y: -2 }} className="group relative p-3 rounded-xl text-left"
                    style={{ background: index === 0 ? "rgba(0,214,143,0.07)" : "rgba(255,255,255,0.03)", border: `1px solid ${index === 0 ? "rgba(0,214,143,0.2)" : "rgba(255,255,255,0.06)"}` }}>
                    <button type="button" onClick={() => onAdvisorCandidateSelected(candidate)} className="w-full text-left pr-7">
                      <div className="flex items-center justify-between gap-2">
                        <span className="text-[11px] font-semibold text-[#DCDCF0]">{candidate.label}</span>
                        {index === 0 && <span className="text-[8px] font-mono text-[#00D68F]">ЛУЧШИЙ</span>}
                      </div>
                      <div className="text-[9px] font-mono text-[#55557A] mt-1">{candidate.summary}</div>
                      <div className="text-[9px] font-mono text-[#3A3A5E] mt-1">
                        {candidate.latencyMs != null ? `Среднее ${candidate.latencyMs} мс` : "Без замера задержки"}
                        {candidate.score != null ? ` · score ${candidate.score}` : ""}
                      </div>
                      <div className="text-[9px] font-semibold text-[#A99BFF] mt-2">Выбрать этот профиль →</div>
                    </button>
                    <motion.button type="button" whileTap={{ scale: 0.82 }} disabled={favoriteLimitReached}
                      onClick={() => onFavoriteToggle(candidate, !favorite)}
                      title={favoriteLimitReached ? "Можно добавить максимум 5 профилей" : favorite ? "Убрать с дашборда" : "Добавить на дашборд"}
                      className={`absolute right-2.5 top-2.5 w-7 h-7 rounded-lg flex items-center justify-center transition-all ${favorite ? "opacity-100" : "opacity-0 group-hover:opacity-100"} disabled:cursor-not-allowed`}>
                      <Star size={15} fill={favorite ? "#F7B955" : "transparent"} style={{ color: favorite ? "#F7B955" : favoriteLimitReached ? "#34344F" : "#77779B", filter: favorite ? "drop-shadow(0 0 6px rgba(247,185,85,0.45))" : "none" }} />
                    </motion.button>
                  </motion.div>
                );
              })}
            </div>
          </motion.div>
        )}
      </GlassCard>

      <GlassCard className="p-5">
        <div className="flex items-center justify-between gap-4">
          <div className="flex items-center gap-3 min-w-0">
            <div className="w-9 h-9 rounded-xl flex items-center justify-center flex-shrink-0"
              style={{ background: hosts.applied ? "rgba(0,214,143,0.09)" : "rgba(255,255,255,0.04)", border: `1px solid ${hosts.applied ? "rgba(0,214,143,0.2)" : "rgba(255,255,255,0.06)"}` }}>
              <FileCode2 size={16} style={{ color: hosts.applied ? "#00D68F" : "#3A3A5E" }} />
            </div>
            <div className="min-w-0">
              <div className="text-[12px] font-semibold text-[#D8D8E8]">Системный hosts</div>
              <div className="text-[10px] font-mono text-[#4A4A6A] mt-1 truncate">
                {hosts.applied ? `Telegram fallback: ${hosts.telegramFallbackCount ?? 0} · Telegram DNS: ${hosts.telegramDnsCount ?? 0} · Roblox CDN: ${hosts.robloxCdnCount ?? 0}` : hosts.detail || "Не применён"}
              </div>
              {hosts.updatedAt && <div className="text-[9px] font-mono text-[#2A2A44] mt-0.5">Обновлён: {new Date(hosts.updatedAt).toLocaleString("ru-RU")}</div>}
            </div>
          </div>
          <button type="button" onClick={onRestoreHosts} className="px-3 py-2 rounded-lg text-[10px] font-semibold transition-colors"
            style={{ color: hosts.applied ? "#FF8A9A" : "#5A5A7A", background: hosts.applied ? "rgba(255,77,109,0.08)" : "rgba(255,255,255,0.035)", border: "1px solid rgba(255,255,255,0.07)" }}>
            Восстановить hosts сейчас
          </button>
        </div>
      </GlassCard>

      <GlassCard className="p-5">
        <div className="flex items-center justify-between gap-3 mb-3">
          <SectionLabel>Избранные профили · {favoriteProfiles.length}/5</SectionLabel>
          <button type="button" onClick={onOpenProfiles} className="text-[9px] font-semibold text-[#A99BFF] hover:text-[#C9C4FF] transition-colors">
            Открыть каталог →
          </button>
        </div>
        {favoriteProfiles.length === 0 ? (
          <motion.button type="button" onClick={onOpenProfiles} whileHover={{ y: -1 }} whileTap={{ scale: 0.995 }}
            className="w-full py-5 rounded-xl flex flex-col items-center justify-center text-center"
            style={{ background: "linear-gradient(135deg,rgba(108,92,231,0.07),rgba(255,255,255,0.02))", border: "1px dashed rgba(108,92,231,0.25)" }}>
            <Star size={19} className="text-[#69638E] mb-2" />
            <span className="text-[11px] font-semibold text-[#8884AA]">Добавьте любимые стратегии из каталога</span>
            <span className="text-[9px] font-mono text-[#454563] mt-1">Здесь можно закрепить до пяти профилей</span>
          </motion.button>
        ) : (
          <div className="grid grid-cols-5 gap-2">
            {favoriteProfiles.map((profile) => {
              const item = profileCatalogItem(profile);
              const selected = bypassLevel === profile.level && bypassTuning === profile.tuning && (profile.mode ?? "standard") === compatibilityMode;
              return (
                <motion.div key={profileKey(profile)} whileHover={{ y: -2 }} className="group relative min-w-0 rounded-xl"
                  style={{ background: selected ? "rgba(108,92,231,0.18)" : "rgba(255,255,255,0.035)", border: `1px solid ${selected ? "rgba(108,92,231,0.45)" : "rgba(255,255,255,0.065)"}` }}>
                  <button type="button" onClick={() => onProfileSelected(profile)} className="w-full p-3 pr-7 text-left min-w-0">
                    <div className="text-[11px] font-semibold truncate" style={{ color: selected ? "#D1CCFF" : "#8585A5" }}>{item.levelLabel}</div>
                    <div className="text-[8px] font-mono mt-1 truncate text-[#4A4A6A]">{item.tuningLabel}</div>
                  </button>
                  <motion.button type="button" whileTap={{ scale: 0.82 }} onClick={() => onFavoriteToggle(profile, false)} title="Убрать с дашборда"
                    className="absolute right-1.5 top-1.5 w-6 h-6 rounded-md flex items-center justify-center opacity-0 group-hover:opacity-100 transition-opacity">
                    <Star size={13} fill="#F7B955" className="text-[#F7B955]" />
                  </motion.button>
                </motion.div>
              );
            })}
          </div>
        )}
        <Divider />
        <SectionLabel>Какие сервисы запускать</SectionLabel>
        <div className="grid grid-cols-5 gap-2">
          {[
            ["youtube", "YouTube", ytSelected, "#FF4D6D"],
            ["discord", "Discord", dcSelected, "#5865F2"],
            ["telegram", "Telegram", tgSelected, "#29B6F6"],
            ["web", "Web-версии", webSelected, "#6C5CE7"],
            ["gamefilter", "Game Filter", gameFilterSelected, "#F7B955"],
          ].map(([id, label, selected, color]) => (
            <div key={String(id)} className="flex items-center justify-between gap-1.5 px-2.5 py-2.5 rounded-xl text-left min-w-0"
              style={{ background: selected ? `${color}14` : "rgba(255,255,255,0.03)", border: `1px solid ${selected ? `${color}45` : "rgba(255,255,255,0.06)"}` }}>
              <span className="text-[10px] font-semibold truncate" style={{ color: selected ? "#EEEEF8" : "#4A4A6A" }}>{label}</span>
              <Toggle checked={Boolean(selected)} onChange={(value) => onServiceSelected(String(id), value)} size="sm" />
            </div>
          ))}
        </div>
        <p className="text-[10px] font-mono text-[#3A3A5E] mt-3">Галочки только выбирают сервисы. Запуск происходит главной кнопкой выше.</p>
      </GlassCard>

      {/* ── Status Cards ── */}
      <div className="grid grid-cols-3 gap-3">
        {statusCards.map((c) => (
          <GlassCard key={c.id} className="p-4">
            <div className="flex items-center justify-between mb-3">
              <div
                className="w-9 h-9 rounded-xl flex items-center justify-center text-lg"
                style={{
                  background: c.active ? `${c.iconColor}15` : "rgba(255,255,255,0.04)",
                  color: c.active ? c.iconColor : "#2A2A44",
                }}
              >
                <c.Icon size={18} />
              </div>
              <Dot on={c.active} pulse={c.active} />
            </div>

            <div
              className="font-bold text-sm text-white mb-1"
              style={{ fontFamily: "'Plus Jakarta Sans', sans-serif" }}
            >
              {c.label}
            </div>

            <div className="flex items-center justify-between">
              <span
                className="text-[11px] font-mono px-1.5 py-0.5 rounded"
                style={{
                  background: c.active ? `${c.subColor}18` : "rgba(255,255,255,0.04)",
                  color: c.active ? c.subColor : "#2A2A44",
                }}
              >
                {c.sub}
              </span>
              {c.active && (
                <span className="text-[11px] font-mono text-[#3A3A5E]">
                  {c.ping}
                </span>
              )}
            </div>
          </GlassCard>
        ))}
      </div>

      {dcSelected && health.discord && !["healthy", "partial", "checking"].includes(health.discord.state) && (
        <div className="px-4 py-3 rounded-xl text-[10px] font-mono text-[#8A7080]"
          style={{ background: "rgba(255,77,109,0.055)", border: "1px solid rgba(255,77,109,0.12)" }}>
          Discord API или voice недоступен. После проверки профилей можно вручную очистить Cache/Code Cache Discord; VortexPass не удаляет их автоматически, чтобы не затронуть пользовательские данные.
        </div>
      )}

      {/* ── Log ── */}
      <LogConsole logs={logs} />
    </div>
  );
}

// ═══════════════════════════════════════════════════════════════════════════════
// TAB 2 — YOUTUBE & DISCORD
// ═══════════════════════════════════════════════════════════════════════════════

function YouTubeTab({
  ytActive,
  dcActive,
  ytSelected,
  dcSelected,
  onToggleYt,
  onToggleDc,
  metrics,
  webBypass,
  onToggleWeb,
}: {
  ytActive: boolean;
  dcActive: boolean;
  ytSelected: boolean;
  dcSelected: boolean;
  onToggleYt: (v: boolean) => void;
  onToggleDc: (v: boolean) => void;
  metrics: Metrics;
  webBypass: boolean;
  onToggleWeb: (value: boolean) => void;
}) {
  const services = [
    {
      id: "yt",
      name: "YouTube",
      Icon: YouTubeIcon,
      iconColor: "#FF4D6D",
      desc: "youtube.com · youtu.be · googlevideo.com",
      active: ytActive,
      selected: ytSelected,
      onToggle: onToggleYt,
      badge: ytActive && metrics.youtubePing != null ? `${metrics.youtubePing} ms` : null,
    },
    {
      id: "dc",
      name: "Discord",
      Icon: DiscordIcon,
      iconColor: "#5865F2",
      desc: "discord.com · discordapp.com · голосовые каналы",
      active: dcActive,
      selected: dcSelected,
      onToggle: onToggleDc,
      badge: dcActive && metrics.discordPing != null ? `${metrics.discordPing} ms` : null,
    },
  ];

  return (
    <div className="flex flex-col gap-5">
      <div>
        <h2
          className="text-[22px] font-bold text-white"
          style={{ fontFamily: "'Plus Jakarta Sans', sans-serif" }}
        >
          YouTube & Discord
        </h2>
        <p className="text-sm text-[#3A3A5E] mt-1">
          Выбор сервисов; общий уровень обхода настраивается на дашборде
        </p>
      </div>

      {services.map((s) => (
        <GlassCard
          key={s.id}
          className="p-5"
          glow={s.active}
          style={
            s.active
              ? {
                  boxShadow: `0 0 0 1px ${s.iconColor}30, 0 8px 32px rgba(0,0,0,0.3)`,
                  border: `1px solid ${s.iconColor}35`,
                }
              : {}
          }
        >
          {/* Service header */}
          <div className="flex items-center gap-3">
            <div
              className="w-10 h-10 rounded-xl flex items-center justify-center text-xl transition-all duration-300 flex-shrink-0"
              style={{
                background: s.active ? `${s.iconColor}18` : "rgba(255,255,255,0.05)",
                color: s.active ? s.iconColor : "#2A2A44",
              }}
            >
              <s.Icon size={21} />
            </div>
            <div className="flex-1">
              <div className="flex items-center gap-2">
                <span
                  className="font-bold text-white"
                  style={{ fontFamily: "'Plus Jakarta Sans', sans-serif" }}
                >
                  {s.name}
                </span>
                <Dot on={s.active} pulse={s.active} />
                {s.badge && (
                  <span
                    className="text-[10px] font-mono px-1.5 py-0.5 rounded"
                    style={{
                      background: `${s.iconColor}18`,
                      color: s.iconColor,
                    }}
                  >
                    {s.badge}
                  </span>
                )}
              </div>
              <div className="text-[11px] text-[#3A3A5E] font-mono mt-0.5">
                {s.desc}
              </div>
            </div>
            <Toggle checked={s.selected} onChange={s.onToggle} />
          </div>
        </GlassCard>
      ))}

      {/* Web bypass checkbox */}
      <GlassCard className="p-5">
        <SectionLabel>Браузер</SectionLabel>
        <CheckBox
          checked={webBypass}
          onChange={onToggleWeb}
          label="Обход блокировок Web-версий в браузере"
          sub="Включает системный DPI-обход winws/WinDivert для приложений и браузеров без расширений"
        />

        {webBypass && (
          <motion.div
            initial={{ opacity: 0, y: -4 }}
            animate={{ opacity: 1, y: 0 }}
            className="mt-4 flex items-center gap-2 px-3 py-2 rounded-xl"
            style={{ background: "rgba(108,92,231,0.08)", border: "1px solid rgba(108,92,231,0.2)" }}
          >
            <FileCode2 size={12} className="text-[#6C5CE7] flex-shrink-0" />
            <span className="text-[11px] font-mono text-[#6060A0]">
               winws / WinDivert · системный трафик
            </span>
            <span
              className="ml-auto text-[10px] font-mono px-1.5 py-0.5 rounded"
              style={{ background: "rgba(0,214,143,0.1)", color: "#00D68F" }}
            >
               {ytActive && dcActive ? "Активен" : "Ожидание запуска"}
            </span>
          </motion.div>
        )}
      </GlassCard>
    </div>
  );
}

// ═══════════════════════════════════════════════════════════════════════════════
// TAB 3 — TELEGRAM
// ═══════════════════════════════════════════════════════════════════════════════

function TelegramTab({
  tgActive,
  tgSelected,
  webActive,
  onToggleTg,
  onToggleWeb,
  onConfigureDesktop,
  pending,
}: {
  tgActive: boolean;
  tgSelected: boolean;
  webActive: boolean;
  onToggleTg: (v: boolean) => void;
  onToggleWeb: (v: boolean) => void;
  onConfigureDesktop: () => void;
  pending: boolean;
}) {
  const tgDesktop = tgSelected;
  const tgWeb = webActive;
  const applying = false;
  const applied = tgActive;

  const handleApply = () => onConfigureDesktop();

  return (
    <div className="flex flex-col gap-5">
      <div>
        <h2
          className="text-[22px] font-bold text-white"
          style={{ fontFamily: "'Plus Jakarta Sans', sans-serif" }}
        >
          Telegram
        </h2>
        <p className="text-sm text-[#3A3A5E] mt-1">
          Системный DPI-обход для Telegram Desktop и Telegram Web
        </p>
      </div>

      {/* Master toggle */}
      <GlassCard
        className="p-5 flex items-center gap-4"
        glow={tgActive}
        style={
          tgActive
            ? { border: "1px solid rgba(41,182,246,0.35)", boxShadow: "0 0 0 1px rgba(41,182,246,0.12), 0 8px 32px rgba(0,0,0,0.3)" }
            : {}
        }
      >
        <div
          className="w-12 h-12 rounded-2xl flex items-center justify-center text-2xl flex-shrink-0 transition-all duration-300"
          style={{ background: tgActive ? "rgba(41,182,246,0.12)" : "rgba(255,255,255,0.05)" }}
        >
          <TelegramIcon size={25} className="text-[#29B6F6]" />
        </div>
        <div className="flex-1">
          <div className="flex items-center gap-2">
            <span
              className="font-bold text-white"
              style={{ fontFamily: "'Plus Jakarta Sans', sans-serif" }}
            >
              Telegram DPI bypass
            </span>
            <Dot on={tgActive} pulse={tgActive} />
            <span
              className="text-[11px] font-mono"
              style={{ color: tgActive ? "#00D68F" : "#2A2A44" }}
            >
              {tgActive ? "Активен" : "Отключён"}
            </span>
          </div>
          <div className="text-[11px] font-mono text-[#3A3A5E] mt-0.5">
            winws · WinDivert · системный трафик
          </div>
        </div>
        <Toggle checked={tgSelected} onChange={onToggleTg} disabled={pending} />
      </GlassCard>

      {/* Telegram Desktop card */}
      <GlassCard className="p-5">
        <div className="flex items-start gap-4">
          <div
            className="w-10 h-10 rounded-xl flex items-center justify-center flex-shrink-0 text-lg"
            style={{
              background: tgDesktop ? "rgba(41,182,246,0.12)" : "rgba(255,255,255,0.05)",
            }}
          >
            <MonitorDot size={19} className="text-[#6C9CFF]" />
          </div>
          <div className="flex-1">
            <div className="flex items-center justify-between gap-3">
              <div>
                <div
                  className="font-semibold text-sm text-white"
                  style={{ fontFamily: "'Plus Jakarta Sans', sans-serif" }}
                >
                  Telegram Desktop
                </div>
                <div className="text-[11px] text-[#3A3A5E] mt-0.5">
                  Локальный MTProto → WebSocket/TLS маршрут для стабильной работы Desktop
                </div>
              </div>
              <Toggle
                checked={tgDesktop}
                onChange={onToggleTg}
                size="sm"
                disabled={pending}
              />
            </div>

            <Divider />

            {/* Proxy details */}
            <div className="grid grid-cols-3 gap-2 mb-4">
              {[
                { label: "Движок", value: "TG WS Proxy" },
                { label: "Адрес", value: "127.0.0.1" },
                { label: "Порт", value: "1443" },
              ].map((f) => (
                <div
                  key={f.label}
                  className="px-3 py-2 rounded-xl"
                  style={{ background: "rgba(0,0,0,0.2)", border: "1px solid rgba(255,255,255,0.06)" }}
                >
                  <div className="text-[9px] font-mono text-[#2A2A44] uppercase tracking-widest mb-0.5">
                    {f.label}
                  </div>
                  <div className="text-[12px] font-mono text-[#6060A0]">{f.value}</div>
                </div>
              ))}
            </div>

            {/* Apply button */}
            <motion.button
              whileHover={{ scale: tgDesktop ? 1.02 : 1 }}
              whileTap={{ scale: tgDesktop ? 0.97 : 1 }}
              onClick={tgDesktop && !applying ? handleApply : undefined}
              disabled={!tgDesktop || applying}
              className="w-full flex items-center justify-center gap-2.5 py-2.5 rounded-xl text-sm font-semibold transition-all duration-200"
              style={{
                fontFamily: "'Plus Jakarta Sans', sans-serif",
                background: applied
                  ? "rgba(0,214,143,0.12)"
                  : tgDesktop
                  ? "linear-gradient(135deg, rgba(41,182,246,0.25), rgba(108,92,231,0.25))"
                  : "rgba(255,255,255,0.04)",
                color: applied ? "#00D68F" : tgDesktop ? "#B5AEFC" : "#2A2A44",
                border: applied
                  ? "1px solid rgba(0,214,143,0.3)"
                  : tgDesktop
                  ? "1px solid rgba(108,92,231,0.35)"
                  : "1px solid rgba(255,255,255,0.06)",
                opacity: !tgDesktop ? 0.4 : 1,
              }}
            >
              {applying ? (
                <motion.span
                  animate={{ rotate: 360 }}
                  transition={{ repeat: Infinity, duration: 0.9, ease: "linear" }}
                >
                  <RefreshCw size={14} />
                </motion.span>
              ) : applied ? (
                <CheckCircle2 size={14} />
              ) : (
                <ExternalLink size={14} />
              )}
              {applying
                ? "Запуск защиты…"
                : applied
                ? "Переподключить Telegram Desktop"
                : "Сначала запустите защиту Telegram"}
            </motion.button>

            {applied && (
              <motion.p
                initial={{ opacity: 0, y: -4 }}
                animate={{ opacity: 1, y: 0 }}
                className="text-center text-[10px] font-mono text-[#3A3A5E] mt-2"
              >
                Если Telegram откроет окно прокси, подтвердите подключение. Сообщения остаются зашифрованными Telegram.
              </motion.p>
            )}
          </div>
        </div>
      </GlassCard>

      {/* Telegram Web card */}
      <GlassCard className="p-5">
        <div className="flex items-start gap-4">
          <div
            className="w-10 h-10 rounded-xl flex items-center justify-center flex-shrink-0 text-lg"
            style={{
              background: tgWeb ? "rgba(41,182,246,0.1)" : "rgba(255,255,255,0.05)",
            }}
          >
            <Globe size={19} className="text-[#29B6F6]" />
          </div>
          <div className="flex-1">
            <div className="flex items-center justify-between gap-3">
              <div>
                <div
                  className="font-semibold text-sm text-white"
                  style={{ fontFamily: "'Plus Jakarta Sans', sans-serif" }}
                >
                  Telegram Web
                </div>
                <div className="text-[11px] text-[#3A3A5E] mt-0.5">
                  Обрабатывается системным DPI-движком без расширений браузера
                </div>
              </div>
              <Toggle checked={tgWeb} onChange={onToggleWeb} size="sm" disabled={pending} />
            </div>

            <Divider />

            {/* PAC info */}
            <div
              className="flex items-center gap-3 px-3 py-2.5 rounded-xl transition-all duration-200"
              style={{
                background: tgWeb ? "rgba(0,180,216,0.07)" : "rgba(255,255,255,0.03)",
                border: `1px solid ${tgWeb ? "rgba(0,180,216,0.2)" : "rgba(255,255,255,0.06)"}`,
                opacity: tgWeb ? 1 : 0.4,
              }}
            >
              <FileCode2 size={13} className="text-[#00B4D8] flex-shrink-0" />
              <div className="flex-1 min-w-0">
                <div className="text-[10px] font-mono text-[#2A2A44] uppercase tracking-widest mb-0.5">
                  Метод
                </div>
                <div className="text-[12px] font-mono text-[#5A5A8A] truncate">
                  winws / WinDivert
                </div>
              </div>
              <span
                className="text-[10px] font-mono px-1.5 py-0.5 rounded flex-shrink-0"
                style={{
                  background: tgWeb ? "rgba(0,214,143,0.1)" : "rgba(255,255,255,0.05)",
                  color: tgWeb ? "#00D68F" : "#2A2A44",
                }}
              >
                {tgWeb ? "Активен" : "Выкл"}
              </span>
            </div>

            <p className="text-[11px] text-[#2A2A44] mt-3 leading-relaxed">
              При включении PAC-скрипт автоматически прописывается в реестр Windows (Internet Settings) и применяется ко всем браузерам, открывающим web.telegram.org.
            </p>
          </div>
        </div>
      </GlassCard>

    </div>
  );
}

// ═══════════════════════════════════════════════════════════════════════════════
// TAB 4 — CLOUDFLARE WARP
// ═══════════════════════════════════════════════════════════════════════════════

function WarpTab({ info, onToggle, onInstall, onRefresh, onPerformance, onOpen, onRestore, onRetry, onRegister, onCopyDiagnostics, onSettings, onOptimize, onProfileSave, onProfileToggle, onProfileDelete }: {
  info: WarpInfo;
  onToggle: (value: boolean) => void;
  onInstall: () => void;
  onRefresh: () => void;
  onPerformance: () => void;
  onOpen: () => void;
  onRestore: () => void;
  onRetry: () => void;
  onRegister: () => void;
  onCopyDiagnostics: () => void;
  onSettings: (value: Record<string, unknown>) => void;
  onOptimize: () => void;
  onProfileSave: (profile: Partial<WarpRouteProfile>) => void;
  onProfileToggle: (id: string, value: boolean) => void;
  onProfileDelete: (id: string) => void;
}) {
  const [newProfile, setNewProfile] = useState({ name: "", kind: "site" as "site" | "game", connection: "auto", domains: "", ipRanges: "", executablePath: "" });
  const connected = info.state === "connected";
  const color = info.state === "error" ? "#FF4D6D" : connected ? "#00D68F" : info.pending ? "#FFB340" : "#62AFFF";
  const stateLabel = info.state === "notInstalled" ? "Не установлен" : info.state === "disconnected" ? "Выключен" :
    info.state === "connecting" ? "Подключение" : info.state === "connected" ? "Подключён" : "Ошибка";

  return (
    <div className="space-y-5 max-w-5xl mx-auto">
      <GlassCard className="p-6" glow={connected}>
        <div className="flex items-center justify-between gap-5">
          <div className="flex items-center gap-4 min-w-0">
            <div className="w-12 h-12 rounded-2xl flex items-center justify-center" style={{ background: `${color}18`, border: `1px solid ${color}40` }}>
              <Cloud size={24} style={{ color }} />
            </div>
            <div className="min-w-0">
              <div className="text-xl font-bold text-white" style={{ fontFamily: "'Plus Jakarta Sans', sans-serif" }}>Cloudflare WARP</div>
              <div className="text-[11px] font-mono mt-1" style={{ color }}>{stateLabel} · {info.detail}</div>
            </div>
          </div>
          <Toggle checked={connected} onChange={onToggle} disabled={info.pending || !info.installed} />
        </div>
        {info.progress != null && info.pending && (
          <div className="mt-4 h-1.5 rounded-full overflow-hidden bg-white/5"><div className="h-full bg-[#62AFFF] transition-all" style={{ width: `${Math.round(info.progress * 100)}%` }} /></div>
        )}
        <div className="grid grid-cols-4 gap-2 mt-4">
          {[
            ["Точка", info.diagnostics?.pop?.code ?? info.diagnostics?.coloCode ?? "—"],
            ["Регион", info.diagnostics?.pop ? `${info.diagnostics.pop.city}, ${info.diagnostics.pop.country}` : info.diagnostics?.traceLocationCode ?? "—"],
            ["Пинг", info.diagnostics?.latencyMs != null ? `${info.diagnostics.latencyMs} мс` : "—"],
            ["Внешний IP", info.diagnostics?.externalIp ?? "—"],
          ].map(([label, value]) => <div key={label} className="min-w-0 px-3 py-2 rounded-xl bg-black/20 border border-white/[.045]"><div className="text-[8px] font-mono text-[#454568]">{label}</div><div className="text-[10px] font-semibold text-[#B8B8D0] mt-0.5 truncate" title={value}>{value}</div></div>)}
        </div>
      </GlassCard>

      {!info.installed && (
        <GlassCard className="p-5">
          <SectionLabel>Установка официального клиента</SectionLabel>
          <p className="text-[11px] text-[#66668A] leading-relaxed mb-4">Установщик загружается с сервера Cloudflare и запускается только после проверки цифровой подписи Cloudflare, Inc. Для системного драйвера потребуется подтверждение Windows UAC.</p>
          <button type="button" disabled={info.pending} onClick={onInstall} className="w-full py-3 rounded-xl text-sm font-semibold text-white disabled:opacity-50" style={{ background: "linear-gradient(135deg,#0078D4,#6C5CE7)" }}>
            {info.pending ? "Подождите…" : "Скачать и установить Cloudflare WARP"}
          </button>
        </GlassCard>
      )}

      {info.installed && info.registered === false && (
        <GlassCard className="p-5">
          <SectionLabel>Первичная настройка</SectionLabel>
          <p className="text-[11px] text-[#66668A] leading-relaxed mb-4">Для первого подключения нужна бесплатная регистрация Consumer WARP. VortexPass передаст согласие официальному warp-cli и не будет сохранять идентификаторы, ключи или лицензию.</p>
          <button type="button" disabled={info.pending} onClick={onRegister} className="w-full py-3 rounded-xl text-sm font-semibold text-white disabled:opacity-50" style={{ background: "linear-gradient(135deg,#0078D4,#6C5CE7)" }}>
            Принять условия Cloudflare и зарегистрировать
          </button>
        </GlassCard>
      )}

      <GlassCard className="relative z-40 p-5">
        <div className="flex items-center justify-between gap-3"><SectionLabel>Режим и протокол</SectionLabel><button type="button" disabled={info.pending || !info.installed} onClick={onOptimize} className="px-3 py-2 rounded-xl text-[10px] font-semibold text-[#AFCFFF] bg-[#62AFFF12] border border-[#62AFFF2D]">Автоподбор</button></div>
        <div className="grid grid-cols-2 gap-3">
          <div><div className="text-[9px] font-mono text-[#555577] mb-1.5">Маршрутизация</div><Select value={info.routingMode ?? "smart"} onChange={(routingMode) => onSettings({ routingMode })} options={[{value:"smart",label:"Умный гибрид"},{value:"full",label:"Полный туннель"},{value:"proxy",label:"Локальный proxy"},{value:"dnsOnly",label:"Только DNS"}]} /></div>
          <div><div className="text-[9px] font-mono text-[#555577] mb-1.5">Протокол</div><Select value={info.protocolPreference ?? "auto"} onChange={(protocol) => onSettings({ protocol })} options={[{value:"auto",label:"Авто"},{value:"masque",label:"MASQUE"},{value:"wireguard",label:"WireGuard"}]} /></div>
          <div><div className="text-[9px] font-mono text-[#555577] mb-1.5">MASQUE</div><Select value={info.masquePreference ?? "h3-with-h2-fallback"} onChange={(masque) => onSettings({ masque })} options={[{value:"h3-with-h2-fallback",label:"HTTP/3 + fallback"},{value:"h3-only",label:"Только HTTP/3"},{value:"h2-only",label:"Только HTTP/2"}]} /></div>
          <div><div className="text-[9px] font-mono text-[#555577] mb-1.5">DNS WARP</div><Select value={info.dnsMode ?? "auto"} onChange={(dnsMode) => onSettings({ dnsMode })} options={[{value:"auto",label:"Авто / UDP"},{value:"doh",label:"DNS over HTTPS"},{value:"dot",label:"DNS over TLS"},{value:"system",label:"Системный DNS"}]} /></div>
        </div>
        {info.optimization && <div className="mt-3 p-3 rounded-xl bg-black/20 text-[10px] font-mono text-[#75BFFF]">{info.optimization.reason}</div>}
        <p className="text-[9px] text-[#454560] mt-3">В режиме Auto VortexPass сравнивает MASQUE и WireGuard для текущей сети и откатывает настройку при потере соединения.</p>
      </GlassCard>

      <GlassCard className="relative z-30 p-5">
        <SectionLabel>Профили маршрутизации</SectionLabel>
        <div className="space-y-2">{(info.profiles ?? []).map((profile) => <div key={profile.id} className="p-3 rounded-xl bg-black/20 border border-white/[.05]">
          <div className="flex items-center gap-3"><Toggle checked={profile.enabled} onChange={(value) => onProfileToggle(profile.id, value)} size="sm"/><div className="min-w-0 flex-1"><div className="text-[11px] font-semibold text-white truncate">{profile.name}</div><div className="text-[8px] font-mono text-[#555577]">{profile.kind === "game" ? "Игра / UDP" : "Web / TCP"} · {profile.connection}</div></div><Select value={profile.connection} onChange={(connection) => onProfileSave({ ...profile, connection: connection as WarpRouteProfile["connection"] })} options={[{value:"auto",label:"Авто"},{value:"direct",label:"Прямой + winws"},...(profile.kind === "site" ? [{value:"warpProxy",label:"WARP proxy"}] : []),{value:"warpSystem",label:"Системный WARP"}]} />{!profile.builtInId && <button onClick={() => onProfileDelete(profile.id)} className="text-[9px] text-[#FF748C]">Удалить</button>}</div>
          {profile.domains.length > 0 && <div className="text-[8px] font-mono text-[#3F3F5F] mt-2 truncate">{profile.domains.join(", ")}</div>}
        </div>)}</div>
        <div className="mt-4 pt-4 border-t border-white/[.06] space-y-2">
          <div className="grid grid-cols-2 gap-2"><input value={newProfile.name} onChange={(e)=>setNewProfile({...newProfile,name:e.target.value})} placeholder="Название профиля" className="px-3 py-2 rounded-xl bg-black/20 border border-white/[.08] text-xs text-white"/><Select value={newProfile.kind} onChange={(kind)=>setNewProfile({...newProfile,kind:kind as "site"|"game"})} options={[{value:"site",label:"Сайт"},{value:"game",label:"Игра"}]} /></div>
          <textarea value={newProfile.domains} onChange={(e)=>setNewProfile({...newProfile,domains:e.target.value})} placeholder="Домены, по одному в строке" className="w-full h-16 px-3 py-2 rounded-xl bg-black/20 border border-white/[.08] text-xs text-white resize-none"/>
          {newProfile.kind === "game" && <><input value={newProfile.executablePath} onChange={(e)=>setNewProfile({...newProfile,executablePath:e.target.value})} placeholder="Путь к .exe (для подписи и диагностики)" className="w-full px-3 py-2 rounded-xl bg-black/20 border border-white/[.08] text-xs text-white"/><textarea value={newProfile.ipRanges} onChange={(e)=>setNewProfile({...newProfile,ipRanges:e.target.value})} placeholder="IP или CIDR для прямых исключений" className="w-full h-12 px-3 py-2 rounded-xl bg-black/20 border border-white/[.08] text-xs text-white resize-none"/></>}
          <button disabled={!newProfile.name.trim()} onClick={()=>{onProfileSave({name:newProfile.name,kind:newProfile.kind,connection:newProfile.connection as WarpRouteProfile["connection"],enabled:false,domains:newProfile.domains.split(/[,\n]/),ipRanges:newProfile.ipRanges.split(/[,\n]/),executablePath:newProfile.executablePath});setNewProfile({name:"",kind:"site",connection:"auto",domains:"",ipRanges:"",executablePath:""});}} className="w-full py-2.5 rounded-xl text-[11px] font-semibold text-[#D8E9FF] bg-[#62AFFF16] border border-[#62AFFF36] disabled:opacity-40">Создать профиль</button>
        </div>
        <p className="text-[9px] text-[#454560] mt-3">Discord voice и игровой UDP не направляются выборочно через SOCKS5. «Системный WARP» действует на весь компьютер.</p>
        {(info.routingConflicts ?? []).map((value) => <div key={value} className="mt-2 text-[9px] font-mono text-[#FF748C]">Конфликт: {value}</div>)}
        {(info.routingWarnings ?? []).map((value) => <div key={value} className="mt-2 text-[9px] font-mono text-[#FFB340]">{value}</div>)}
      </GlassCard>

      {info.state === "error" && (
        <GlassCard className="p-5" style={{ border: "1px solid rgba(255,77,109,.28)" }}>
          <SectionLabel>Почему не подключилось</SectionLabel>
          <div className="text-[11px] font-mono text-[#FF8CA0] leading-relaxed">{info.error ?? info.stage ?? "Cloudflare WARP не сообщил причину."}</div>
          <div className="grid grid-cols-2 gap-2 mt-4">
            <button type="button" disabled={info.pending || !info.registered} onClick={onRetry} className="py-2.5 rounded-xl text-[11px] font-semibold text-[#D8E9FF] bg-[#62AFFF16] border border-[#62AFFF36]">Повторить подключение</button>
            <button type="button" onClick={onCopyDiagnostics} className="py-2.5 rounded-xl text-[11px] font-semibold text-[#FFD1D9] bg-[#FF4D6D0D] border border-[#FF4D6D24]">Скопировать безопасную диагностику</button>
          </div>
        </GlassCard>
      )}

      <GlassCard className="relative z-20 p-5">
        <div className="flex items-center justify-between gap-3"><SectionLabel>Производительность WARP</SectionLabel><button type="button" disabled={info.pending} onClick={onPerformance} className="px-3 py-2 rounded-xl text-[10px] font-semibold text-[#AFCFFF] bg-[#62AFFF12] border border-[#62AFFF2D]">Проверить производительность</button></div>
        <p className="text-[10px] text-[#5A5A7A] leading-relaxed">Три коротких измерения Cloudflare, Telegram Web, YouTube и Discord. С выключенным WARP сохраняется прямой baseline; подключение не переключается автоматически.</p>
        {info.performance && <div className="mt-3 space-y-2">
          <div className="text-[10px] font-mono" style={{ color: info.performance.verdict === "Worse" ? "#FF8C66" : info.performance.verdict === "Better" ? "#00D68F" : "#AFCFFF" }}>{info.performance.recommendation ?? info.performance.summary}</div>
          <div className="grid grid-cols-4 gap-2">{Object.entries(info.performance.endpoints).map(([id, endpoint]) => <div key={id} className="p-3 rounded-xl bg-black/20"><div className="text-[9px] font-mono text-[#3A3A5E]">{endpoint.label}</div><div className="text-[11px] font-semibold mt-1" style={{ color: endpoint.available ? "#A9A9C8" : "#FF748C" }}>{endpoint.available && endpoint.medianLatencyMs != null ? `${endpoint.medianLatencyMs} мс` : "Нет ответа"}</div><div className="text-[8px] font-mono text-[#454560] mt-1">{endpoint.available && endpoint.jitterMs != null ? `разброс ${endpoint.jitterMs} мс` : endpoint.detail}</div></div>)}</div>
        </div>}
      </GlassCard>

      <GlassCard className="relative z-10 p-5">
        <div className="flex items-center justify-between"><SectionLabel>Диагностика</SectionLabel><span className="text-[9px] font-mono text-[#3A3A5E]">{info.version ?? "warp-cli —"}</span></div>
        <div className="grid grid-cols-4 gap-2 mt-3">
          {[
            ["Маршрут", info.diagnostics?.routeActive ? "WARP" : "Не подтверждён"],
            ["DNS", info.diagnostics?.dnsAvailable ? "Доступен" : "Нет данных"],
            ["Задержка", info.diagnostics?.latencyMs != null ? `${info.diagnostics.latencyMs} мс` : "—"],
            ["Внешний IP", info.diagnostics?.externalIp ?? "—"],
          ].map(([label, value]) => <div key={label} className="p-3 rounded-xl bg-black/20"><div className="text-[9px] font-mono text-[#3A3A5E]">{label}</div><div className="text-[11px] font-semibold text-[#A9A9C8] mt-1 truncate">{value}</div></div>)}
        </div>
        <div className="grid grid-cols-4 gap-2 mt-2">
          {[
            ["Служба", info.serviceRunning ? "Работает" : "Не найдена"],
            ["Протокол", info.protocol ?? "—"],
            ["Режим", info.effectiveRoutingMode ?? info.routingMode ?? "—"],
            ["Bootstrap", info.bootstrapActive ? "Активен" : "Выключен"],
          ].map(([label, value]) => <div key={label} className="p-3 rounded-xl bg-black/20"><div className="text-[9px] font-mono text-[#3A3A5E]">{label}</div><div className="text-[11px] font-semibold text-[#A9A9C8] mt-1 truncate">{value}</div></div>)}
        </div>
        {info.telegramCompatibilityActive && <div className="mt-3 text-[10px] font-mono text-[#62AFFF]">Telegram: режим совместимости WARP активен, прямые desync/CDN-закрепления временно отключены.</div>}
        {info.error && <div className="mt-3 text-[10px] font-mono text-[#FF748C]">{info.error}</div>}
        <div className="grid grid-cols-2 gap-2 mt-4">
          <button type="button" disabled={info.pending} onClick={onRefresh} className="py-2.5 rounded-xl text-[11px] font-semibold text-[#AFCFFF] bg-[#62AFFF12] border border-[#62AFFF2D]">Проверить WARP</button>
          <button type="button" disabled={!info.installed} onClick={onOpen} className="py-2.5 rounded-xl text-[11px] font-semibold text-[#B9B6D8] bg-white/[.035] border border-white/[.07]">Открыть клиент</button>
          <button type="button" disabled={!connected || info.pending} onClick={onRestore} className="py-2.5 rounded-xl text-[11px] font-semibold text-[#FF9AAA] bg-[#FF4D6D0D] border border-[#FF4D6D24]">Обычный маршрут</button>
          <button type="button" onClick={onCopyDiagnostics} className="py-2.5 rounded-xl text-[11px] font-semibold text-[#B9B6D8] bg-white/[.035] border border-white/[.07]">Копировать диагностику</button>
        </div>
      </GlassCard>
    </div>
  );
}

// ═══════════════════════════════════════════════════════════════════════════════
// TAB 5 — GAME FILTER
// ═══════════════════════════════════════════════════════════════════════════════

function GameFilterTab({
  selected,
  active,
  pending,
  info,
  onToggle,
  onChange,
}: {
  selected: boolean;
  active: boolean;
  pending: boolean;
  info: GameFilterInfo;
  onToggle: (value: boolean) => void;
  onChange: (value: GameFilterInfo) => void;
}) {
  const [open, setOpen] = useState(false);
  const games = [
    { id: "phasmophobia", name: "Phasmophobia", subtitle: "Gameplay — напрямую · Vivox voice — мягкая обработка", color: "#F7B955" },
    { id: "roblox", name: "Roblox", subtitle: "Gameplay — напрямую · Web/CDN — обработка", color: "#6AA8FF" },
    { id: "deadbydaylight", name: "Dead by Daylight", subtitle: "Gameplay UDP — напрямую · авторизация — обработка", color: "#E35B68" },
  ] as const;
  const selectedGames = games.filter((game) => info.selectedGames.includes(game.id));
  const setGame = (id: "phasmophobia" | "roblox" | "deadbydaylight", value: boolean) => {
    const next = new Set(info.selectedGames);
    if (value) next.add(id); else next.delete(id);
    onChange({ ...info, selectedGames: games.map((game) => game.id).filter((gameId) => next.has(gameId)) });
  };

  return (
    <div className="flex flex-col gap-5">
      <div>
        <h2 className="text-[22px] font-bold text-white" style={{ fontFamily: "'Plus Jakarta Sans', sans-serif" }}>Game Filter</h2>
        <p className="text-sm text-[#3A3A5E] mt-1">Узкие правила DPI для сетевого трафика выбранных игр</p>
      </div>

      <GlassCard className="p-5 flex items-center gap-4" glow={active}>
        <div className="w-12 h-12 rounded-2xl flex items-center justify-center flex-shrink-0"
          style={{ background: active ? "rgba(247,185,85,0.14)" : "rgba(255,255,255,0.05)", color: active ? "#F7B955" : "#3A3A5E" }}>
          <GameFilterIcon size={25} />
        </div>
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-2">
            <span className="font-bold text-white" style={{ fontFamily: "'Plus Jakarta Sans', sans-serif" }}>Игровой профиль</span>
            <Dot on={active} pulse={active} />
          </div>
          <div className="text-[11px] font-mono text-[#3A3A5E] mt-1">
            {active ? `Активен · ${info.protocol === "all" ? "UDP + TCP" : info.protocol.toUpperCase()} · ${selectedGames.map((game) => game.name).join(", ") || "игры не выбраны"}` : "Применяется только после главной кнопки запуска"}
          </div>
        </div>
        <Toggle checked={selected} onChange={onToggle} disabled={pending} />
      </GlassCard>

      <GlassCard className="p-5">
        <SectionLabel>Выбор игры</SectionLabel>
        <div className="relative">
          <motion.button type="button" onClick={() => setOpen((value) => !value)} whileTap={{ scale: 0.99 }}
            className="w-full flex items-center justify-between gap-3 px-4 py-3.5 rounded-xl text-left"
            style={{ background: open ? "rgba(108,92,231,0.12)" : "rgba(255,255,255,0.045)", border: `1px solid ${open ? "rgba(108,92,231,0.38)" : "rgba(255,255,255,0.08)"}` }}>
            <div className="flex items-center gap-3">
              <Gamepad2 size={18} className="text-[#F7B955]" />
              <div>
                <div className="text-[13px] font-semibold text-[#EEEEF8]">{selectedGames.length ? selectedGames.map((game) => game.name).join(", ") : "Нажмите, чтобы выбрать нужные игры"}</div>
                <div className="text-[10px] font-mono text-[#4A4A6A] mt-0.5">{selectedGames.length ? `${selectedGames.length} ${selectedGames.length === 1 ? "игра выбрана" : "игры выбраны"}` : "Игры не выбраны"}</div>
              </div>
            </div>
            <motion.span animate={{ rotate: open ? 180 : 0 }}><ChevronDown size={15} className="text-[#6A6A8A]" /></motion.span>
          </motion.button>
          <AnimatePresence>
            {open && (
              <motion.div initial={{ opacity: 0, y: -7, scale: 0.985 }} animate={{ opacity: 1, y: 0, scale: 1 }} exit={{ opacity: 0, y: -7, scale: 0.985 }}
                className="absolute z-40 left-0 right-0 mt-2 p-2 rounded-xl"
                style={{ background: "rgba(18,18,28,0.98)", border: "1px solid rgba(255,255,255,0.1)", boxShadow: "0 16px 42px rgba(0,0,0,0.5)" }}>
                {games.map((game) => {
                  const checked = info.selectedGames.includes(game.id);
                  return <button key={game.id} type="button" onClick={() => setGame(game.id, !checked)} className="w-full flex items-center gap-3 px-3 py-3 rounded-lg text-left hover:bg-white/5">
                    <div className="w-8 h-8 rounded-lg flex items-center justify-center" style={{ background: `${game.color}1A`, color: game.color }}>
                      {game.id === "roblox" ? <span className="text-[13px] font-black leading-none">R</span> : game.id === "deadbydaylight" ? <span className="text-[11px] font-black leading-none">DBD</span> : <GameFilterIcon size={18} />}
                    </div>
                    <div className="flex-1"><div className="text-[13px] font-semibold text-white">{game.name}</div><div className="text-[10px] font-mono text-[#4A4A6A]">{game.subtitle}</div></div>
                    <span className="w-5 h-5 rounded-md flex items-center justify-center" style={{ background: checked ? "#6C5CE7" : "rgba(255,255,255,0.05)", border: "1px solid rgba(255,255,255,0.1)" }}>
                      {checked && <Check size={12} className="text-white" strokeWidth={3} />}
                    </span>
                  </button>;
                })}
              </motion.div>
            )}
          </AnimatePresence>
        </div>

        <Divider />
        <SectionLabel>Протокол</SectionLabel>
        <div className="grid grid-cols-3 gap-2">
          {[
            ["udp", "UDP", "Для Phasmophobia не ломает игровой UDP"],
            ["tcp", "TCP", "Домены сервисов и WebSocket"],
            ["all", "UDP + TCP", "Стабильный режим по умолчанию"],
          ].map(([id, label, sub]) => (
            <button key={id} type="button" onClick={() => onChange({ ...info, protocol: id as GameFilterInfo["protocol"] })}
              className="px-3 py-3 rounded-xl text-left transition-all"
              style={{ background: info.protocol === id ? "rgba(247,185,85,0.12)" : "rgba(255,255,255,0.035)", border: `1px solid ${info.protocol === id ? "rgba(247,185,85,0.32)" : "rgba(255,255,255,0.06)"}` }}>
              <div className="text-[12px] font-semibold" style={{ color: info.protocol === id ? "#FFD28A" : "#666684" }}>{label}</div>
              <div className="text-[9px] font-mono text-[#3A3A5E] mt-1">{sub}</div>
            </button>
          ))}
        </div>
        <p className="text-[10px] font-mono text-[#4A4A6A] mt-4">Изменения сохраняются до следующего запуска защиты. Для каждой игры используются отдельные узкие правила; общий игровой трафик и чужие сессии не перехватываются.</p>
      </GlassCard>
    </div>
  );
}

// ═══════════════════════════════════════════════════════════════════════════════
// TAB 5 — PROFILES
// ═══════════════════════════════════════════════════════════════════════════════

function ProfilesTab({ favorites, currentLevel, currentTuning, compatibility, pending, onSelect, onLaunch, onFavoriteToggle, onCompatibility }: {
  favorites: FavoriteProfile[];
  currentLevel: string;
  currentTuning: number;
  compatibility: CompatibilityInfo;
  pending: boolean;
  onSelect: (profile: FavoriteProfile) => void;
  onLaunch: (profile: FavoriteProfile) => void;
  onFavoriteToggle: (profile: FavoriteProfile, favorite: boolean) => void;
  onCompatibility: (mode: "standard" | "mobile", launch: boolean) => void;
}) {
  const [query, setQuery] = useState("");
  const normalizedQuery = query.trim().toLocaleLowerCase("ru-RU");
  const profiles = createProfileCatalog(compatibility.mode).filter((profile) =>
    !normalizedQuery || [profile.label, profile.short, profile.description, profile.level]
      .some((value) => value.toLocaleLowerCase("ru-RU").includes(normalizedQuery)),
  );

  return (
    <div className="flex flex-col gap-5">
      <div>
        <h1 className="text-[23px] font-bold text-white" style={{ fontFamily: "'Plus Jakarta Sans', sans-serif" }}>Профили</h1>
        <p className="text-[12px] text-[#67678B] mt-1">Стратегии подключения, профиль текущей сети и избранное для дашборда</p>
      </div>

      <GlassCard className="p-5 overflow-hidden relative">
        <div className="absolute inset-0 pointer-events-none" style={{ background: "radial-gradient(circle at 100% 0%,rgba(69,197,245,0.10),transparent 45%)" }} />
        <div className="relative flex items-center justify-between gap-4 mb-4">
          <div className="flex items-center gap-3 min-w-0">
            <div className="w-10 h-10 rounded-xl flex items-center justify-center text-[#45C5F5] flex-shrink-0"
              style={{ background: "rgba(69,197,245,0.09)", border: "1px solid rgba(69,197,245,0.22)" }}>
              <Smartphone size={18} />
            </div>
            <div className="min-w-0">
              <div className="text-[13px] font-semibold text-[#E2E2EF]">Профиль текущей сети</div>
              <div className="text-[9px] font-mono text-[#55557A] mt-1 truncate">
                Настройка запоминается отдельно для этого подключения
              </div>
            </div>
          </div>
          {compatibility.remembered && <span className="text-[8px] font-mono text-[#00D68F]">ЗАПОМНЕН</span>}
        </div>
        <div className="relative grid grid-cols-2 gap-3">
          {([
            { id: "standard", title: "Обычная сеть", detail: "Текущие полные правила для домашнего провайдера" },
            { id: "mobile", title: "Мобильная совместимость", detail: "Узкие правила без широкого Game Filter и Telegram IP-закрепления" },
          ] as const).map((item) => {
            const selected = compatibility.mode === item.id;
            return (
              <div key={item.id} className="relative rounded-xl p-3 pr-12"
                style={{ background: selected ? "rgba(69,197,245,0.11)" : "rgba(255,255,255,0.03)", border: `1px solid ${selected ? "rgba(69,197,245,0.34)" : "rgba(255,255,255,0.06)"}` }}>
                <button type="button" disabled={pending} onClick={() => onCompatibility(item.id, false)} className="w-full text-left disabled:opacity-45">
                  <div className="text-[11px] font-semibold" style={{ color: selected ? "#BDEBFF" : "#8585A5" }}>{item.title}</div>
                  <div className="text-[9px] font-mono text-[#4A4A6A] mt-1 leading-relaxed">{item.detail}</div>
                  {item.id === "mobile" && selected && (
                    <div className="text-[8px] font-mono text-[#6C9CB5] mt-2">
                      YouTube: {compatibility.youtubeTransport === "quic" ? "QUIC подтверждён" : compatibility.youtubeTransport === "http2" ? "TCP/HTTP2 fallback" : "автовыбор"}
                    </div>
                  )}
                </button>
                <motion.button type="button" whileTap={{ scale: 0.88 }} disabled={pending}
                  onClick={() => onCompatibility(item.id, true)} title="Выбрать и запустить защиту"
                  className="absolute right-3 top-3 w-8 h-8 rounded-lg flex items-center justify-center disabled:opacity-35"
                  style={{ color: selected ? "#BDEBFF" : "#74779A", background: selected ? "rgba(69,197,245,0.14)" : "rgba(255,255,255,0.035)", border: "1px solid rgba(255,255,255,0.08)" }}>
                  <Play size={13} fill="currentColor" />
                </motion.button>
              </div>
            );
          })}
        </div>
        {compatibility.mode === "mobile" && (
          <div className="relative mt-3 rounded-xl px-3 py-2.5 text-[9px] font-mono text-[#6C9CB5]"
            style={{ background: "rgba(69,197,245,0.045)", border: "1px solid rgba(69,197,245,0.12)" }}>
            <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
              <span>{compatibility.providerName || "Оператор определяется"}</span>
              <span>· Мобильный профиль</span>
              <span>· Telegram: {compatibility.optimizeState === "confirmed" ? "маршрут подтверждён" : compatibility.optimizeState === "cached" ? "сохранённый маршрут" : compatibility.optimizeState === "fallback" ? "резервный маршрут" : compatibility.optimizeState === "partial" ? "частичный маршрут" : compatibility.optimizeState === "manual" ? "нужна проверка" : "подбор маршрута"}</span>
              <span>· Discord: {compatibility.discordVoiceApplied === "minimal" ? "минимальный voice" : compatibility.discordVoiceApplied ?? "авто"}</span>
              <span>· YouTube: {compatibility.youtubeTransport === "quic" ? "QUIC" : compatibility.youtubeTransport === "http2" ? "HTTP/2" : "авто"}</span>
              <span>· DNS: {compatibility.dnsState === "ready" ? `защищён${compatibility.dnsResolver ? ` (${compatibility.dnsResolver})` : ""}` : compatibility.dnsState === "portBusy" ? "порт 53 занят" : compatibility.dnsState === "dohUnavailable" ? "DoH недоступен" : compatibility.dnsState === "restored" ? "восстановлен" : "подготовка"}</span>
            </div>
            <div className="flex flex-wrap items-center gap-x-3 gap-y-1 mt-1 text-[#7897A8]">
              <span>Домены winws: {compatibility.winwsDomainCount ?? "—"}</span>
              <span>{compatibility.pinnedIpDetail ?? "Закреплённые IP: нет — безопасный DNS-маршрут"}</span>
            </div>
            {compatibility.routes && <div className="grid grid-cols-1 sm:grid-cols-2 gap-1.5 mt-2">
              {["youtube", "discord", "telegram"].map((id) => {
                const route = compatibility.routes?.[id];
                if (!route) return null;
                const label = id === "youtube" ? "YouTube" : id === "discord" ? "Discord Web" : "Telegram Web";
                const routeLabel = route.route === "warpProxy" ? "WARP-прокси" : route.route === "http2+winws" ? "HTTP/2 + winws" : "winws";
                return <div key={id} className="rounded-lg px-2 py-1.5 bg-black/15" title={route.detail}>
                  <span className={route.state === "confirmed" ? "text-[#63D9A6]" : route.state === "manual" ? "text-[#FFB340]" : "text-[#8297A5]"}>{label} — {routeLabel}</span>
                </div>;
              })}
            </div>}
            {(compatibility.dnsDetail || compatibility.optimizeDetail) && <div className="mt-1 text-[#536D80] truncate" title={compatibility.dnsDetail || compatibility.optimizeDetail}>{compatibility.dnsDetail || compatibility.optimizeDetail}</div>}
            <button type="button" disabled={pending} onClick={() => postNativePayload({ type: "compatibility.route.refresh" })}
              className="mt-2 px-2.5 py-1.5 rounded-lg text-[9px] text-[#9CC9DD] bg-[#45C5F50B] border border-[#45C5F526] disabled:opacity-40">
              Перепроверить маршруты
            </button>
          </div>
        )}
      </GlassCard>

      <GlassCard className="p-5 overflow-hidden relative">
        <div className="absolute inset-0 pointer-events-none" style={{ background: "radial-gradient(circle at 8% 0%,rgba(177,36,57,0.09),transparent 38%),radial-gradient(circle at 92% 0%,rgba(108,92,231,0.11),transparent 42%)" }} />
        <div className="relative flex items-center gap-3">
          <motion.div animate={{ color: query ? "#C5BFFF" : "#68688C" }} className="w-10 h-10 rounded-xl flex items-center justify-center flex-shrink-0"
            style={{ background: "rgba(108,92,231,0.09)", border: "1px solid rgba(108,92,231,0.2)" }}>
            <Search size={18} />
          </motion.div>
          <div className="relative flex-1">
            <input value={query} onChange={(event) => setQuery(event.target.value)} autoComplete="off"
              placeholder="Найдите нужный профиль…"
              className="w-full h-11 px-4 rounded-xl bg-white/[0.035] border border-white/[0.07] text-[12px] text-[#EEEEF8] placeholder:text-[#4B4B6B] outline-none focus:border-[#6C5CE7]/60 focus:ring-2 focus:ring-[#6C5CE7]/15 transition-all" />
          </div>
          <div className="px-3 py-2 rounded-xl text-[10px] font-mono text-[#77779B] bg-white/[0.035] border border-white/[0.06] whitespace-nowrap">
            Избранное {favorites.length}/5
          </div>
        </div>
      </GlassCard>

      <div className="flex items-center justify-between px-1">
        <span className="text-[10px] font-mono text-[#55557A]">Найдено профилей: {profiles.length}</span>
        <span className="text-[9px] font-mono text-[#3A3A5E]">Запуск использует отмеченные на дашборде сервисы</span>
      </div>

      <motion.div layout className="grid grid-cols-2 gap-3">
        <AnimatePresence mode="popLayout">
          {profiles.map((profile) => {
            const favorite = favorites.some((item) => profileKey(item) === profileKey(profile));
            const selected = currentLevel === profile.level && currentTuning === profile.tuning;
            const favoriteLimitReached = !favorite && favorites.length >= 5;
            return (
              <motion.div layout key={profileKey(profile)} initial={{ opacity: 0, y: 8 }} animate={{ opacity: 1, y: 0 }} exit={{ opacity: 0, scale: 0.97 }}
                whileHover={{ y: -2 }} className="group relative p-4 rounded-2xl overflow-hidden"
                style={{ background: selected ? "linear-gradient(135deg,rgba(108,92,231,0.16),rgba(255,255,255,0.035))" : "rgba(255,255,255,0.032)", border: `1px solid ${selected ? "rgba(108,92,231,0.42)" : "rgba(255,255,255,0.07)"}` }}>
                <button type="button" onClick={() => onSelect(profile)} className="w-full text-left pr-20">
                  <div className="flex items-center gap-2">
                    <span className="text-[13px] font-semibold" style={{ color: selected ? "#D3CFFF" : "#E2E2EF", fontFamily: "'Plus Jakarta Sans', sans-serif" }}>{profile.levelLabel}</span>
                    {selected && <span className="px-1.5 py-0.5 rounded text-[7px] font-mono text-[#BEB7FF] bg-[#6C5CE7]/15 border border-[#6C5CE7]/25">ВЫБРАН</span>}
                  </div>
                  <div className="text-[10px] font-semibold text-[#8D86BD] mt-1">{profile.tuningLabel}</div>
                  <div className="text-[9px] font-mono text-[#55557A] mt-2 leading-relaxed">{profile.description}</div>
                </button>
                <div className="absolute right-3 top-3 flex items-center gap-1.5">
                  <motion.button type="button" whileHover={{ scale: 1.06 }} whileTap={{ scale: 0.88 }} disabled={pending}
                    onClick={() => onLaunch(profile)} title="Применить и запустить защиту"
                    className="w-8 h-8 rounded-lg flex items-center justify-center disabled:opacity-35"
                    style={{ color: "#BEB8FF", background: "rgba(108,92,231,0.15)", border: "1px solid rgba(108,92,231,0.3)" }}>
                    <Play size={14} fill="currentColor" />
                  </motion.button>
                  <motion.button type="button" whileHover={{ scale: 1.08 }} whileTap={{ scale: 0.82 }} disabled={favoriteLimitReached}
                    onClick={() => onFavoriteToggle(profile, !favorite)}
                    title={favoriteLimitReached ? "Можно добавить максимум 5 профилей" : favorite ? "Убрать с дашборда" : "Добавить на дашборд"}
                    className="w-8 h-8 rounded-lg flex items-center justify-center disabled:cursor-not-allowed"
                    style={{ background: favorite ? "rgba(247,185,85,0.12)" : "rgba(255,255,255,0.035)", border: `1px solid ${favorite ? "rgba(247,185,85,0.3)" : "rgba(255,255,255,0.07)"}` }}>
                    <Star size={15} fill={favorite ? "#F7B955" : "transparent"} style={{ color: favorite ? "#F7B955" : favoriteLimitReached ? "#34344F" : "#77779B", filter: favorite ? "drop-shadow(0 0 7px rgba(247,185,85,0.5))" : "none", transition: "all .2s ease" }} />
                  </motion.button>
                </div>
              </motion.div>
            );
          })}
        </AnimatePresence>
      </motion.div>

      {profiles.length === 0 && (
        <GlassCard className="p-10 text-center">
          <Search size={22} className="mx-auto text-[#454563]" />
          <div className="text-[12px] font-semibold text-[#77779A] mt-3">Профили не найдены</div>
          <button type="button" onClick={() => setQuery("")} className="text-[10px] text-[#A99BFF] mt-2">Сбросить поиск</button>
        </GlassCard>
      )}
    </div>
  );
}

// ═══════════════════════════════════════════════════════════════════════════════
// TAB 6 — SETTINGS
// ═══════════════════════════════════════════════════════════════════════════════

function DomainListEditor({ title, description, values, emptyText, onSave }: {
  title: string;
  description: string;
  values: string[];
  emptyText: string;
  onSave: (values: string[]) => void;
}) {
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(values.join("\n"));
  useEffect(() => { if (!editing) setDraft(values.join("\n")); }, [values, editing]);
  const parsed = draft.split(/\r?\n|,/).map((value) => value.trim()).filter(Boolean);

  return (
    <GlassCard className="p-5">
      <div className="flex items-start justify-between gap-4">
        <div>
          <div className="text-sm font-semibold text-white" style={{ fontFamily: "'Plus Jakarta Sans', sans-serif" }}>{title}</div>
          <div className="text-[11px] text-[#4A4A6A] mt-1">{description}</div>
        </div>
        <button type="button" onClick={() => setEditing((value) => !value)} className="flex items-center gap-2 px-3 py-2 rounded-lg text-[11px] font-semibold"
          style={{ color: editing ? "#B5AEFC" : "#77779A", background: editing ? "rgba(108,92,231,0.12)" : "rgba(255,255,255,0.04)", border: "1px solid rgba(255,255,255,0.07)" }}>
          <Pencil size={12} />{editing ? "Закрыть" : "Редактировать"}
        </button>
      </div>
      <div className="mt-3 text-[10px] font-mono text-[#4A4A6A]">{values.length ? `${values.length} доменов сохранено` : emptyText}</div>
      <AnimatePresence>
        {editing && (
          <motion.div initial={{ opacity: 0, height: 0 }} animate={{ opacity: 1, height: "auto" }} exit={{ opacity: 0, height: 0 }} className="overflow-visible">
            <textarea value={draft} onChange={(event) => setDraft(event.target.value)} spellCheck={false}
              placeholder="example.com\ncdn.example.com"
              className="domain-list-textarea w-full h-44 mt-4 px-3 py-3 rounded-xl resize-y text-[11px] font-mono text-[#B8B8D0] outline-none focus:ring-2 focus:ring-[#6C5CE7]/40"
              style={{ background: "rgba(0,0,0,0.24)", border: "1px solid rgba(255,255,255,0.08)" }} />
            <div className="flex items-center justify-between gap-3 mt-3">
              <span className="text-[10px] font-mono text-[#3A3A5E]">Один домен на строку, без https:// и путей</span>
              <button type="button" onClick={() => { onSave(parsed); setEditing(false); }} className="flex items-center gap-2 px-3.5 py-2 rounded-lg text-[11px] font-semibold text-[#C7C2FF]"
                style={{ background: "rgba(108,92,231,0.16)", border: "1px solid rgba(108,92,231,0.3)" }}><Save size={12} />Сохранить</button>
            </div>
            <div className="text-[10px] font-mono text-[#6A6280] mt-3">После сохранения список будет применён при следующем запуске защиты.</div>
          </motion.div>
        )}
      </AnimatePresence>
    </GlassCard>
  );
}

function UpdatePromptModal({ update }: { update: UpdateInfo }) {
  const busy = update.state === "preparing" || update.state === "installing" || update.state === "downloading";
  const visible = update.state === "availablePrompt" || busy || update.state === "failed";
  if (!visible) return null;

  const failed = update.state === "failed";
  const description = failed
    ? (update.error ?? "Проверьте подключение к GitHub или updater.log.")
    : "Можно обновиться сейчас или применить обновление при следующем запуске.";

  return (
    <AnimatePresence>
      <motion.div
        className="fixed inset-0 z-50 flex items-center justify-center px-6"
        initial={{ opacity: 0 }}
        animate={{ opacity: 1 }}
        exit={{ opacity: 0 }}
        style={{ background: "rgba(4,4,10,0.62)", backdropFilter: "blur(18px)", WebkitBackdropFilter: "blur(18px)" }}
      >
        <motion.div
          initial={{ opacity: 0, y: 18, scale: 0.96 }}
          animate={{ opacity: 1, y: 0, scale: 1 }}
          exit={{ opacity: 0, y: 10, scale: 0.98 }}
          transition={{ type: "spring", stiffness: 320, damping: 30 }}
          className="w-full max-w-[430px] rounded-[26px] p-5"
          style={{
            background: "linear-gradient(145deg, rgba(23,23,34,0.96), rgba(12,12,20,0.98))",
            border: "1px solid rgba(255,255,255,0.12)",
            boxShadow: "0 24px 70px rgba(0,0,0,0.55), 0 0 45px rgba(108,92,231,0.18)",
          }}
        >
          <div className="flex items-start gap-4">
            <div className="w-12 h-12 rounded-2xl flex items-center justify-center flex-shrink-0"
              style={{ background: failed ? "rgba(255,107,107,0.12)" : "rgba(108,92,231,0.16)", border: `1px solid ${failed ? "rgba(255,107,107,0.28)" : "rgba(108,92,231,0.36)"}` }}>
              {busy ? (
                <motion.span animate={{ rotate: 360 }} transition={{ repeat: Infinity, duration: 0.9, ease: "linear" }}>
                  <RefreshCw size={22} className="text-[#B5AEFC]" />
                </motion.span>
              ) : failed ? (
                <AlertTriangle size={22} className="text-[#FF6B6B]" />
              ) : (
                <Download size={22} className="text-[#B5AEFC]" />
              )}
            </div>
            <div className="min-w-0 flex-1">
              <div className="text-xl font-bold text-white" style={{ fontFamily: "'Plus Jakarta Sans', sans-serif" }}>
                {failed ? "Обновление не установлено" : "Вышло новое обновление"}
              </div>
              <div className="text-[12px] font-mono text-[#8B87B8] mt-1">v{update.availableVersion ?? "—"}</div>
              <div className="text-sm text-[#8D91B5] mt-3 leading-relaxed">
                {busy ? (update.state === "installing" ? "Updater запущен. VortexPass закроется и откроется уже в новой версии." : "Скачиваю и проверяю ZIP-архив обновления…") : description}
              </div>
            </div>
          </div>

          <div className="flex items-center justify-end gap-3 mt-6">
            {failed ? (
              <button type="button" onClick={() => postNativeMessage("update.dismissError")} className="px-4 py-2.5 rounded-xl text-sm font-semibold text-[#C7C2FF]"
                style={{ background: "rgba(108,92,231,0.13)", border: "1px solid rgba(108,92,231,0.28)" }}>
                Закрыть
              </button>
            ) : (
              <>
                <button type="button" disabled={busy} onClick={() => postNativeMessage("update.defer")} className="px-4 py-2.5 rounded-xl text-sm font-semibold transition-opacity disabled:opacity-45"
                  style={{ color: "#B6B7D8", background: "rgba(255,255,255,0.055)", border: "1px solid rgba(255,255,255,0.09)" }}>
                  Не сейчас
                </button>
                <button type="button" disabled={busy} onClick={() => postNativeMessage("update.installNow")} className="px-4 py-2.5 rounded-xl text-sm font-semibold transition-opacity disabled:opacity-45"
                  style={{ color: "#FFFFFF", background: "linear-gradient(135deg, #6C5CE7, #2E8BFF)", border: "1px solid rgba(181,174,252,0.42)", boxShadow: "0 10px 26px rgba(108,92,231,0.28)" }}>
                  Обновиться сейчас
                </button>
              </>
            )}
          </div>
        </motion.div>
      </motion.div>
    </AnimatePresence>
  );
}

function SmoothSelect<T extends string>({
  value,
  onChange,
  options,
  placeholder = "Выберите...",
}: {
  value: T;
  onChange: (val: T) => void;
  options: { id: T; label: string; description?: string; icon?: React.ReactNode }[];
  placeholder?: string;
}) {
  const [open, setOpen] = useState(false);
  const selected = options.find((o) => o.id === value);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      if (ref.current && !ref.current.contains(event.target as Node)) {
        setOpen(false);
      }
    };
    if (open) {
      document.addEventListener("mousedown", handleClickOutside);
      return () => document.removeEventListener("mousedown", handleClickOutside);
    }
  }, [open]);

  return (
    <div className={`relative min-w-[230px] ${open ? "z-50" : "z-10"}`} ref={ref}>
      <motion.button
        type="button"
        onClick={() => setOpen((prev) => !prev)}
        whileTap={{ scale: 0.98 }}
        className="w-full flex items-center justify-between gap-3 px-3.5 py-2.5 rounded-xl text-left transition-all duration-200 cursor-pointer"
        style={{
          background: open ? "rgba(108,92,231,0.16)" : "rgba(255,255,255,0.05)",
          border: `1px solid ${open ? "rgba(108,92,231,0.4)" : "rgba(255,255,255,0.08)"}`,
          boxShadow: open ? "0 0 16px rgba(108,92,231,0.15)" : "none",
        }}
      >
        <div className="flex items-center gap-2.5 min-w-0 flex-1">
          {selected?.icon && <div className="text-[#B5AEFC] flex-shrink-0">{selected.icon}</div>}
          <div className="truncate">
            <div className="text-[12px] font-semibold text-white truncate">
              {selected?.label ?? placeholder}
            </div>
            {selected?.description && (
              <div className="text-[10px] text-[#4A4A6A] truncate mt-0.5">
                {selected.description}
              </div>
            )}
          </div>
        </div>
        <motion.span animate={{ rotate: open ? 180 : 0 }} transition={{ duration: 0.2 }}>
          <ChevronDown size={14} className="text-[#6A6A8A] flex-shrink-0" />
        </motion.span>
      </motion.button>

      <AnimatePresence>
        {open && (
          <motion.div
            initial={{ opacity: 0, y: -6, scale: 0.98 }}
            animate={{ opacity: 1, y: 0, scale: 1 }}
            exit={{ opacity: 0, y: -6, scale: 0.98 }}
            transition={{ duration: 0.15, ease: "easeOut" }}
            className="absolute z-[100] left-0 right-0 mt-1.5 p-1.5 rounded-xl overflow-hidden shadow-2xl"
            style={{
              background: "rgba(18,18,28,0.98)",
              backdropFilter: "blur(20px)",
              WebkitBackdropFilter: "blur(20px)",
              border: "1px solid rgba(255,255,255,0.12)",
              boxShadow: "0 20px 50px rgba(0,0,0,0.6), 0 0 30px rgba(108,92,231,0.15)",
            }}
          >
            <div className="flex flex-col gap-1 max-h-60 overflow-y-auto">
              {options.map((opt) => {
                const isSelected = opt.id === value;
                return (
                  <button
                    key={opt.id}
                    type="button"
                    onClick={() => {
                      onChange(opt.id);
                      setOpen(false);
                    }}
                    className="w-full flex items-center justify-between gap-2.5 px-3 py-2 rounded-lg text-left transition-colors hover:bg-white/5 cursor-pointer"
                    style={{
                      background: isSelected ? "rgba(108,92,231,0.14)" : "transparent",
                    }}
                  >
                    <div className="flex items-center gap-2.5 min-w-0 flex-1">
                      {opt.icon && (
                        <div className={isSelected ? "text-[#B5AEFC]" : "text-[#5A5A7A]"}>
                          {opt.icon}
                        </div>
                      )}
                      <div className="truncate">
                        <div
                          className="text-[12px] font-semibold truncate"
                          style={{ color: isSelected ? "#C7C2FF" : "#E2E2F0" }}
                        >
                          {opt.label}
                        </div>
                        {opt.description && (
                          <div className="text-[10px] text-[#4A4A6A] truncate">
                            {opt.description}
                          </div>
                        )}
                      </div>
                    </div>
                    {isSelected && <Check size={13} className="text-[#B5AEFC] flex-shrink-0" strokeWidth={3} />}
                  </button>
                );
              })}
            </div>
          </motion.div>
        )}
      </AnimatePresence>
    </div>
  );
}

const dnsOptions = [
  {
    id: "auto" as const,
    label: "Автоматически",
    description: "Подбор самого быстрого DNS с минимальным пингом",
    icon: <Sparkles size={14} />,
  },
  {
    id: "google" as const,
    label: "Google Public DNS",
    description: "dns.google · 8.8.8.8 (защита от ТСПУ)",
    icon: <Globe size={14} />,
  },
  {
    id: "cloudflare" as const,
    label: "Cloudflare DNS",
    description: "cloudflare-dns.com · 1.1.1.1 (защита от ТСПУ)",
    icon: <Zap size={14} />,
  },
  {
    id: "quad9" as const,
    label: "Quad9 DNS",
    description: "dns.quad9.net · защищённый резервный маршрут",
    icon: <ShieldCheck size={14} />,
  },
];

const strategyOptions = [
  {
    id: "auto" as const,
    label: "Автоматически",
    description: "Умный подбор стратегии с минимальной задержкой при входе",
    icon: <Sparkles size={14} />,
  },
  {
    id: "light" as const,
    label: "Легкая (Light)",
    description: "Минимальное вмешательство, autottl=2",
    icon: <Zap size={14} />,
  },
  {
    id: "moderate" as const,
    label: "Умеренная (Moderate)",
    description: "Базовый fakedsplit, repeats=4",
    icon: <Zap size={14} />,
  },
  {
    id: "medium" as const,
    label: "Средняя (Medium)",
    description: "Стабильный multisplit, seqovl=681, repeats=6",
    icon: <Zap size={14} />,
  },
  {
    id: "complex" as const,
    label: "Сложная (Complex)",
    description: "Усиленный fakedsplit, repeats=8",
    icon: <Zap size={14} />,
  },
  {
    id: "aggressive" as const,
    label: "Агрессивная (Aggressive)",
    description: "Максимальный desync со stun-фейками, repeats=12",
    icon: <Zap size={14} />,
  },
];

function SettingsTab({
  update,
  domains,
  settings,
  onSettingsChange,
  onSaveDomains,
}: {
  update: UpdateInfo;
  domains: DomainListsInfo;
  settings: SettingsInfo;
  onSettingsChange: (name: keyof SettingsInfo, value: any) => void;
  onSaveDomains: (value: DomainListsInfo) => void;
}) {
  const handleUpdate = () => postNativeMessage(update.state === "available" || update.state === "availablePrompt" ? "update.installNow" : "update.check");

  const updateMeta = {
    idle: { label: "Проверить обновления GitHub", color: "#B5AEFC", bg: "rgba(108,92,231,0.15)", border: "rgba(108,92,231,0.3)" },
    checking: { label: "Проверка обновлений GitHub…", color: "#B5AEFC", bg: "rgba(108,92,231,0.15)", border: "rgba(108,92,231,0.3)" },
    current: { label: "У вас актуальная версия", color: "#00D68F", bg: "rgba(0,214,143,0.1)", border: "rgba(0,214,143,0.3)" },
    available: { label: `Скачать v${update.availableVersion ?? ""} с GitHub`, color: "#FFB340", bg: "rgba(255,179,64,0.1)", border: "rgba(255,179,64,0.3)" },
    availablePrompt: { label: `Доступна v${update.availableVersion ?? ""}`, color: "#FFB340", bg: "rgba(255,179,64,0.1)", border: "rgba(255,179,64,0.3)" },
    preparing: { label: "Подготовка обновления…", color: "#B5AEFC", bg: "rgba(108,92,231,0.15)", border: "rgba(108,92,231,0.3)" },
    deferred: { label: "Обновление установится при следующем запуске", color: "#00D68F", bg: "rgba(0,214,143,0.1)", border: "rgba(0,214,143,0.3)" },
    installing: { label: "Запуск updater…", color: "#B5AEFC", bg: "rgba(108,92,231,0.15)", border: "rgba(108,92,231,0.3)" },
    unavailable: { label: "Повторить проверку GitHub", color: "#FFB340", bg: "rgba(255,179,64,0.1)", border: "rgba(255,179,64,0.3)" },
    failed: { label: "Повторить проверку", color: "#FF6B6B", bg: "rgba(255,107,107,0.1)", border: "rgba(255,107,107,0.3)" },
    downloading: { label: `Скачивание v${update.availableVersion ?? ""}…`, color: "#B5AEFC", bg: "rgba(108,92,231,0.15)", border: "rgba(108,92,231,0.3)" },
  };

  const meta = updateMeta[update.state];

  return (
    <div className="flex flex-col gap-5">
      <div>
        <h2
          className="text-[22px] font-bold text-white"
          style={{ fontFamily: "'Plus Jakarta Sans', sans-serif" }}
        >
          Настройки
        </h2>
        <p className="text-sm text-[#3A3A5E] mt-1">
          Поведение приложения, DNS-сервер и обновление VortexPass
        </p>
      </div>

      {/* Behaviour */}
      <GlassCard className="relative z-30 p-5">
        <SectionLabel>Запуск и поведение</SectionLabel>

        <div className="flex items-center justify-between">
          <div>
            <div
              className="text-sm font-medium text-white"
              style={{ fontFamily: "'Plus Jakarta Sans', sans-serif" }}
            >
              Запускать вместе с Windows
            </div>
            <div className="text-[11px] text-[#3A3A5E] mt-0.5">
              Запуск с правами администратора при входе в систему (Task Scheduler)
            </div>
          </div>
          <Toggle checked={settings.autostart} onChange={(value) => onSettingsChange("autostart", value)} />
        </div>

        <AnimatePresence>
          {settings.autostart && (
            <motion.div
              initial={{ opacity: 0, height: 0 }}
              animate={{ opacity: 1, height: "auto" }}
              exit={{ opacity: 0, height: 0 }}
              transition={{ duration: 0.25, ease: "easeInOut" }}
              className="overflow-hidden"
            >
              <div className="pt-4 flex flex-col gap-4 border-t border-white/[0.06] mt-4">
                <div className="flex items-center justify-between">
                  <div>
                    <div className="text-sm font-medium text-white" style={{ fontFamily: "'Plus Jakarta Sans', sans-serif" }}>
                      Автоматически запускать защиту при старте
                    </div>
                    <div className="text-[11px] text-[#3A3A5E] mt-0.5">
                      Сразу активировать сетевое ускорение при входе в Windows
                    </div>
                  </div>
                  <Toggle
                    checked={settings.startProtectionOnStartup ?? false}
                    onChange={(value) => onSettingsChange("startProtectionOnStartup", value)}
                  />
                </div>

                <div className="flex items-center justify-between gap-4">
                  <div>
                    <div className="text-sm font-medium text-white" style={{ fontFamily: "'Plus Jakarta Sans', sans-serif" }}>
                      Какую стратегию запускать
                    </div>
                    <div className="text-[11px] text-[#3A3A5E] mt-0.5">
                      Автоподбор с минимальной задержкой или фиксированный уровень
                    </div>
                  </div>
                  <SmoothSelect
                    value={settings.startupStrategy ?? "auto"}
                    onChange={(val) => onSettingsChange("startupStrategy", val)}
                    options={strategyOptions}
                  />
                </div>
              </div>
            </motion.div>
          )}
        </AnimatePresence>

        <Divider />

        <div className="flex items-center justify-between">
          <div>
            <div
              className="text-sm font-medium text-white"
              style={{ fontFamily: "'Plus Jakarta Sans', sans-serif" }}
            >
              Сворачивать в системный трей при закрытии
            </div>
            <div className="text-[11px] text-[#3A3A5E] mt-0.5">
              Закрытие окна скрывает приложение в трей, службы продолжают работу
            </div>
          </div>
          <Toggle checked={settings.tray} onChange={(value) => onSettingsChange("tray", value)} />
        </div>
      </GlassCard>

      {/* DNS Configuration */}
      <GlassCard className="relative z-20 p-5">
        <SectionLabel>Сетевые настройки и DNS</SectionLabel>

        <div className="flex items-center justify-between gap-4">
          <div>
            <div
              className="text-sm font-medium text-white"
              style={{ fontFamily: "'Plus Jakarta Sans', sans-serif" }}
            >
              Использовать DNS-сервер
            </div>
            <div className="text-[11px] text-[#3A3A5E] mt-0.5">
              Защищенный DNS over HTTPS (DoH) с десинком TCP 443 против блокировок ТСПУ
            </div>
          </div>
          <SmoothSelect
            value={settings.selectedDnsProvider ?? "auto"}
            onChange={(val) => onSettingsChange("selectedDnsProvider", val)}
            options={dnsOptions}
          />
        </div>
      </GlassCard>

      {/* Application updates */}
      <GlassCard className="relative z-10 p-5">
        <SectionLabel>Обновление приложения</SectionLabel>

        <div className="flex items-start gap-3 mb-5">
          <div
            className="w-10 h-10 rounded-xl flex items-center justify-center flex-shrink-0"
            style={{ background: "rgba(255,255,255,0.04)" }}
          >
            <Github size={18} className="text-[#4A4A6A]" />
          </div>
          <div>
            <div
              className="text-sm font-medium text-white"
              style={{ fontFamily: "'Plus Jakarta Sans', sans-serif" }}
            >
              Репозиторий VortexPass
            </div>
            <div className="text-[11px] font-mono text-[#3A3A5E] mt-0.5">
              github.com/avov53/VortexPass
            </div>
          </div>
        </div>

        {/* Last update info */}
        <div
          className="flex items-center gap-3 px-3 py-2.5 rounded-xl mb-4"
          style={{ background: "rgba(0,0,0,0.2)", border: "1px solid rgba(255,255,255,0.06)" }}
        >
          <Clock size={12} className="text-[#3A3A5E] flex-shrink-0" />
          <div className="flex-1">
            <div className="text-[11px] font-mono text-[#3A3A5E]">
              Последняя успешная проверка
            </div>
            <div
              className="text-xs font-medium text-[#5A5A8A] mt-0.5"
              style={{ fontFamily: "'Plus Jakarta Sans', sans-serif" }}
            >
              {update.error
                ? update.error
                : update.checkedAt
                  ? new Date(update.checkedAt).toLocaleString("ru-RU")
                  : "Проверка ещё не выполнялась"}
            </div>
          </div>
          <span
            className="text-[10px] font-mono px-1.5 py-0.5 rounded"
            style={{
              background: "rgba(108,92,231,0.1)",
              color: "#6C5CE7",
            }}
          >
            v{update.availableVersion ?? update.currentVersion ?? "—"}
          </span>
        </div>

        {/* Check button */}
        <motion.button
          whileHover={["checking", "downloading", "preparing", "installing"].includes(update.state) ? {} : { scale: 1.015, y: -1 }}
          whileTap={["checking", "downloading", "preparing", "installing"].includes(update.state) ? {} : { scale: 0.98 }}
          onClick={handleUpdate}
          disabled={["checking", "downloading", "preparing", "installing", "deferred", "current"].includes(update.state)}
          className="w-full flex items-center justify-center gap-2.5 py-3 rounded-xl font-semibold text-sm transition-all duration-300"
          style={{
            fontFamily: "'Plus Jakarta Sans', sans-serif",
            background: meta.bg,
            color: meta.color,
            border: `1px solid ${meta.border}`,
            boxShadow:
              update.state === "idle" || update.state === "available" || update.state === "availablePrompt"
                ? "0 4px 16px rgba(108,92,231,0.2)"
                : "none",
          }}
        >
          {["checking", "downloading", "preparing", "installing"].includes(update.state) ? (
            <motion.span
              animate={{ rotate: 360 }}
              transition={{ repeat: Infinity, duration: 0.9, ease: "linear" }}
            >
              <RefreshCw size={15} />
            </motion.span>
          ) : update.state === "current" ? (
            <CheckCircle2 size={15} />
          ) : (
            <Github size={15} />
          )}
          {meta.label}
        </motion.button>
      </GlassCard>

      {/* App info */}
      <GlassCard className="relative z-0 p-5">
        <SectionLabel>О приложении</SectionLabel>
        <div className="grid grid-cols-2 gap-3">
          {[
            { label: "Версия", value: `v${update.currentVersion ?? (window as NativeWindow).__vortexRuntime?.app?.version ?? "—"}` },
            { label: "Архитектура", value: "Windows x64" },
            { label: "Компоненты", value: "Локальные, SHA-256" },
            { label: "Платформа", value: (window as NativeWindow).__vortexRuntime?.app?.platform ?? "Windows 10/11" },
          ].map((f) => (
            <div
              key={f.label}
              className="px-3 py-2 rounded-xl"
              style={{ background: "rgba(0,0,0,0.18)", border: "1px solid rgba(255,255,255,0.055)" }}
            >
              <div className="text-[9px] font-mono text-[#2A2A44] uppercase tracking-widest mb-0.5">
                {f.label}
              </div>
              <div
                className="text-sm font-semibold text-[#5A5A8A]"
                style={{ fontFamily: "'Plus Jakarta Sans', sans-serif" }}
              >
                {f.value}
              </div>
            </div>
          ))}
        </div>
      </GlassCard>

      <DomainListEditor
        title="Домены для обхода"
        description="Дополняют встроенные списки YouTube, Discord и Telegram. По умолчанию список пуст."
        values={domains.bypass}
        emptyText="Пользовательские домены не добавлены"
        onSave={(bypass) => onSaveDomains({ ...domains, bypass })}
      />
      <DomainListEditor
        title="Исключённые домены"
        description="Эти сайты не будут обрабатываться правилами DPI; исключения имеют приоритет."
        values={domains.exclusions}
        emptyText="Исключения не заданы"
        onSave={(exclusions) => onSaveDomains({ ...domains, exclusions })}
      />
    </div>
  );
}

// ═══════════════════════════════════════════════════════════════════════════════
// SIDEBAR NAV DATA
// ═══════════════════════════════════════════════════════════════════════════════

const NAV: { id: Tab; Icon: BrandIcon; trailingIcon?: BrandIcon; label: string; sub: string; color: string }[] = [
  { id: "dashboard", Icon: LayoutDashboard, label: "Дашборд", sub: "Статус и управление", color: "#A99BFF" },
  { id: "profiles", Icon: SlidersHorizontal, label: "Профили", sub: "20 стратегий · Избранное", color: "#D0A3FF" },
  { id: "youtube", Icon: YouTubeIcon, trailingIcon: DiscordIcon, label: "YouTube & Discord", sub: "Ускорение сервисов", color: "#FF5B72" },
  { id: "telegram", Icon: TelegramIcon, label: "Telegram", sub: "Desktop · Web", color: "#45C5F5" },
  { id: "warp", Icon: Cloud, label: "WARP", sub: "Cloudflare · Ускоритель", color: "#62AFFF" },
  { id: "gamefilter", Icon: GameFilterIcon, label: "Game Filter", sub: "Игры · UDP/TCP", color: "#F7B955" },
  { id: "settings", Icon: Settings2, label: "Настройки", sub: "Автозапуск · Обновления", color: "#C7C7E8" },
];

// ═══════════════════════════════════════════════════════════════════════════════
// ROOT APP
// ═══════════════════════════════════════════════════════════════════════════════

const SEED_LOGS: LogEntry[] = [
  { ts: "—", level: "info", msg: "Защита выключена. Службы не запущены." },
];

export default function App() {
  const [tab, setTab] = useState<Tab>("dashboard");
  const [protection, setProtection] = useState(false);
  const [protectionPending, setProtectionPending] = useState(false);
  const [ytActive, setYtActive] = useState(false);
  const [dcActive, setDcActive] = useState(false);
  const [tgActive, setTgActive] = useState(false);
  const [gameFilterActive, setGameFilterActive] = useState(false);
  const [ytSelected, setYtSelected] = useState(Boolean((window as NativeWindow).__vortexRuntime?.profiles?.youtube));
  const [dcSelected, setDcSelected] = useState(Boolean((window as NativeWindow).__vortexRuntime?.profiles?.discord));
  const [tgSelected, setTgSelected] = useState(Boolean((window as NativeWindow).__vortexRuntime?.profiles?.telegram));
  const [telegramWebSelected, setTelegramWebSelected] = useState(Boolean(
    (window as NativeWindow).__vortexRuntime?.profiles?.telegramWeb ??
    (window as NativeWindow).__vortexRuntime?.profiles?.web
  ));
  const [webBypass, setWebBypass] = useState(Boolean((window as NativeWindow).__vortexRuntime?.profiles?.web));
  const [gameFilterSelected, setGameFilterSelected] = useState(Boolean((window as NativeWindow).__vortexRuntime?.profiles?.gameFilter));
  const [gameFilter, setGameFilter] = useState<GameFilterInfo>(() => ({
    selectedGames: Array.isArray((window as NativeWindow).__vortexRuntime?.gameFilter?.selectedGames) ? (window as NativeWindow).__vortexRuntime?.gameFilter.selectedGames : [],
    protocol: (["udp", "tcp", "all"].includes((window as NativeWindow).__vortexRuntime?.gameFilter?.protocol) ? (window as NativeWindow).__vortexRuntime?.gameFilter.protocol : "all") as GameFilterInfo["protocol"],
  }));
  const [domains, setDomains] = useState<DomainListsInfo>(() => ({
    bypass: Array.isArray((window as NativeWindow).__vortexRuntime?.domains?.bypass) ? (window as NativeWindow).__vortexRuntime?.domains.bypass : [],
    exclusions: Array.isArray((window as NativeWindow).__vortexRuntime?.domains?.exclusions) ? (window as NativeWindow).__vortexRuntime?.domains.exclusions : [],
  }));
  const [bypassLevel, setBypassLevel] = useState(String((window as NativeWindow).__vortexRuntime?.bypassLevel ?? "medium"));
  const [bypassTuning, setBypassTuning] = useState(Number((window as NativeWindow).__vortexRuntime?.bypassTuning ?? 0));
  const [compatibility, setCompatibility] = useState<CompatibilityInfo>(() => ({
    mode: (window as NativeWindow).__vortexRuntime?.compatibility?.mode === "mobile" ? "mobile" : "standard",
    networkKey: (window as NativeWindow).__vortexRuntime?.compatibility?.networkKey,
    remembered: Boolean((window as NativeWindow).__vortexRuntime?.compatibility?.remembered),
    youtubeTransport: (window as NativeWindow).__vortexRuntime?.compatibility?.youtubeTransport ?? "auto",
    discordVoiceMode: (window as NativeWindow).__vortexRuntime?.compatibility?.discordVoiceMode ?? "auto",
    discordVoiceApplied: (window as NativeWindow).__vortexRuntime?.compatibility?.discordVoiceApplied ?? "minimal",
    providerName: (window as NativeWindow).__vortexRuntime?.compatibility?.providerName,
    telegramRouteCount: Number((window as NativeWindow).__vortexRuntime?.compatibility?.telegramRouteCount ?? 0),
    optimizeState: (window as NativeWindow).__vortexRuntime?.compatibility?.optimizeState ?? "idle",
    optimizeDetail: (window as NativeWindow).__vortexRuntime?.compatibility?.optimizeDetail ?? "",
    dnsState: (window as NativeWindow).__vortexRuntime?.compatibility?.dnsState ?? "idle",
    dnsDetail: (window as NativeWindow).__vortexRuntime?.compatibility?.dnsDetail ?? "",
    dnsResolver: (window as NativeWindow).__vortexRuntime?.compatibility?.dnsResolver ?? "",
    dnsAdapter: (window as NativeWindow).__vortexRuntime?.compatibility?.dnsAdapter ?? "",
    winwsDomainCount: Number((window as NativeWindow).__vortexRuntime?.compatibility?.winwsDomainCount ?? 0),
    pinnedIpCount: Number((window as NativeWindow).__vortexRuntime?.compatibility?.pinnedIpCount ?? 0),
    pinnedIpDetail: (window as NativeWindow).__vortexRuntime?.compatibility?.pinnedIpDetail,
    routes: (window as NativeWindow).__vortexRuntime?.compatibility?.routes,
  }));
  const [favoriteProfiles, setFavoriteProfiles] = useState<FavoriteProfile[]>(() => {
    const profiles = (window as NativeWindow).__vortexRuntime?.favoriteProfiles;
    return Array.isArray(profiles) ? profiles.slice(0, 5).map((profile) => ({
      level: String(profile.level), tuning: Number(profile.tuning ?? 0), mode: profile.mode === "mobile" ? "mobile" as const : "standard" as const,
    })) : [];
  });
  const [health, setHealth] = useState<HealthMap>({});
  const [hosts, setHosts] = useState<HostsInfo>({ applied: false, entryCount: 0, detail: "hosts не применён" });
  const [logs, setLogs] = useState<LogEntry[]>(SEED_LOGS);
  const [metrics, setMetrics] = useState<Metrics>({ uptimeSeconds: 0, trafficBytes: 0, packets: 0, youtubePing: null, discordPing: null, telegramPing: null });
  const [update, setUpdate] = useState<UpdateInfo>({ state: "idle", currentVersion: (window as NativeWindow).__vortexRuntime?.app?.version });
  const [settings, setSettings] = useState<SettingsInfo>({
    autostart: nativeSetting("autostart", false),
    tray: nativeSetting("tray", true),
    selectedDnsProvider: nativeSetting("selectedDnsProvider", "auto"),
    startProtectionOnStartup: nativeSetting("startProtectionOnStartup", false),
    startupStrategy: nativeSetting("startupStrategy", "auto"),
    gameFilterIpsetMode: nativeSetting("gameFilterIpsetMode", "any"),
  });
  const [advisor, setAdvisor] = useState<AdvisorInfo>({ state: "idle" });
  const [warp, setWarp] = useState<WarpInfo>(() => ({
    state: (window as NativeWindow).__vortexRuntime?.warp?.state ?? "notInstalled",
    installed: Boolean((window as NativeWindow).__vortexRuntime?.warp?.installed),
    connected: (window as NativeWindow).__vortexRuntime?.warp?.state === "connected",
    pending: false,
    detail: (window as NativeWindow).__vortexRuntime?.warp?.detail ?? "Проверка клиента WARP",
    registered: Boolean((window as NativeWindow).__vortexRuntime?.warp?.registered),
    serviceRunning: Boolean((window as NativeWindow).__vortexRuntime?.warp?.serviceRunning),
    protocol: (window as NativeWindow).__vortexRuntime?.warp?.protocol,
    routingMode: (window as NativeWindow).__vortexRuntime?.warp?.routingMode ?? "smart",
    protocolPreference: (window as NativeWindow).__vortexRuntime?.warp?.protocolPreference ?? "auto",
    masquePreference: (window as NativeWindow).__vortexRuntime?.warp?.masquePreference ?? "h3-with-h2-fallback",
    dnsMode: (window as NativeWindow).__vortexRuntime?.warp?.dnsMode ?? "auto",
    proxyPort: Number((window as NativeWindow).__vortexRuntime?.warp?.proxyPort ?? 41870),
    profiles: Array.isArray((window as NativeWindow).__vortexRuntime?.warp?.profiles) ? (window as NativeWindow).__vortexRuntime?.warp?.profiles : [],
    bootstrapActive: false,
    scenarios: {
      telegram: Boolean((window as NativeWindow).__vortexRuntime?.warp?.telegram ?? true),
      games: Boolean((window as NativeWindow).__vortexRuntime?.warp?.games),
    },
  }));

  useNativeMessage((message) => {
    if (!message?.type) return;
    if (message.type === "metrics") setMetrics(message as Metrics);
    if (message.type === "update.status") setUpdate(message as UpdateInfo);
    if (message.type === "profile.status") {
      setBypassLevel(String(message.level ?? "medium"));
      setBypassTuning(Number(message.tuning ?? 0));
      if (message.compatibility) setCompatibility(message.compatibility as CompatibilityInfo);
      if (Array.isArray(message.favoriteProfiles)) {
        setFavoriteProfiles(message.favoriteProfiles.slice(0, 5).map((profile: any) => ({
          level: String(profile.level), tuning: Number(profile.tuning ?? 0), mode: profile.mode === "mobile" ? "mobile" : "standard",
        })));
      }
    }
    if (message.type === "health.status") setHealth((message.services ?? {}) as HealthMap);
    if (message.type === "compatibility.optimize.status" || message.type === "compatibility.dns.status" || message.type === "compatibility.route.status") setCompatibility((current) => ({ ...current, ...(message as Partial<CompatibilityInfo>) }));
    if (message.type === "hosts.status") setHosts(message as HostsInfo);
    if (message.type === "settings.status") {
      setSettings((current) => ({
        ...current,
        autostart: Boolean(message.autostart),
        tray: Boolean(message.tray),
        selectedDnsProvider: message.selectedDnsProvider ?? current.selectedDnsProvider ?? "auto",
        startProtectionOnStartup: Boolean(message.startProtectionOnStartup ?? current.startProtectionOnStartup),
        startupStrategy: message.startupStrategy ?? current.startupStrategy ?? "auto",
        gameFilterIpsetMode: message.gameFilterIpsetMode ?? current.gameFilterIpsetMode ?? "any",
      }));
    }
    if (message.type === "advisor.status") setAdvisor(message as AdvisorInfo);
    if (message.type === "warp.status") setWarp(message as WarpInfo);
    if (message.type === "warp.progress") setWarp((current) => ({ ...current, progress: Number(message.progress ?? 0), pending: true }));
    if (message.type === "configuration.status") {
      if (message.gameFilter) setGameFilter(message.gameFilter as GameFilterInfo);
      if (message.domains) setDomains(message.domains as DomainListsInfo);
    }
    if (message.type === "protection.status") {
      setProtection(Boolean(message.enabled));
      setProtectionPending(Boolean(message.pending));
      setYtActive(Boolean(message.youtube));
      setDcActive(Boolean(message.discord));
      setTgActive(Boolean(message.telegram));
      setGameFilterActive(Boolean(message.gameFilter));
      setYtSelected(Boolean(message.requestedYoutube));
      setDcSelected(Boolean(message.requestedDiscord));
      setTgSelected(Boolean(message.requestedTelegram));
      setTelegramWebSelected(Boolean(message.requestedTelegramWeb ?? message.telegramWeb));
      setWebBypass(Boolean(message.requestedWeb ?? message.web));
      setGameFilterSelected(Boolean(message.requestedGameFilter));
    }
    if (message.type === "log") {
      setLogs((current) => [...current.slice(-249), { ts: nowTs(), level: message.level ?? "info", msg: message.message }]);
    }
  });

  const running = [ytActive, dcActive, tgActive, protection && telegramWebSelected, protection && webBypass, gameFilterActive].filter(Boolean).length;
  const advisorBusy = advisor.state === "detecting" || advisor.state === "testing";
  const controlsBusy = protectionPending || advisorBusy;

  // Sync: master protection toggle controls all services
  const handleToggleProtection = () => {
    if (controlsBusy && !protection) return;
    const next = !protection;
    postNativeMessage("protection.set", next);
  };

  const handleProfileSelected = (profile: FavoriteProfile) => {
    if (controlsBusy) return;
    setBypassLevel(profile.level);
    setBypassTuning(profile.tuning);
    if (profile.mode) setCompatibility((current) => ({ ...current, mode: profile.mode! }));
    postNativePayload({ type: "profile.set", level: profile.level, tuning: profile.tuning, mode: profile.mode ?? compatibility.mode });
  };

  const handleFavoriteToggle = (profile: FavoriteProfile, favorite: boolean) => {
    if (favorite && favoriteProfiles.length >= 5) return;
    postNativePayload({ type: "profile.favorite.set", level: profile.level, tuning: profile.tuning, mode: profile.mode ?? compatibility.mode, favorite });
  };

  const handleProfileLaunch = (profile: FavoriteProfile) => {
    if (controlsBusy) return;
    setBypassLevel(profile.level);
    setBypassTuning(profile.tuning);
    setProtectionPending(true);
    if (profile.mode) setCompatibility((current) => ({ ...current, mode: profile.mode! }));
    postNativePayload({ type: "profile.launch", level: profile.level, tuning: profile.tuning, mode: profile.mode ?? compatibility.mode });
  };

  const content: Record<Tab, React.ReactNode> = {
    dashboard: (
      <Dashboard
        protection={protection}
        pending={controlsBusy}
        onToggleProtection={handleToggleProtection}
        ytActive={ytActive}
        dcActive={dcActive}
        tgActive={tgActive}
        ytSelected={ytSelected}
        dcSelected={dcSelected}
        tgSelected={tgSelected}
        webSelected={webBypass}
        gameFilterSelected={gameFilterSelected}
        bypassLevel={bypassLevel}
        bypassTuning={bypassTuning}
        compatibilityMode={compatibility.mode}
        favoriteProfiles={favoriteProfiles}
        health={health}
        hosts={hosts}
        advisor={advisor}
        onRunAdvisor={() => postNativePayload({ type: "advisor.run" })}
        onStopAdvisor={() => postNativePayload({ type: "advisor.stop" })}
        onAdvisorCandidateSelected={(candidate) => {
          if (controlsBusy) return;
          handleProfileSelected(candidate);
        }}
        onProfileSelected={handleProfileSelected}
        onFavoriteToggle={handleFavoriteToggle}
        onOpenProfiles={() => setTab("profiles")}
        onServiceSelected={(serviceId, selected) => {
          if (controlsBusy) return;
          if (serviceId === "youtube") setYtSelected(selected);
          if (serviceId === "discord") setDcSelected(selected);
          if (serviceId === "telegram") setTgSelected(selected);
          if (serviceId === "web") setWebBypass(selected);
          if (serviceId === "gamefilter") setGameFilterSelected(selected);
          postNativeMessage("service.set", selected, serviceId);
        }}
        onRestoreHosts={() => postNativeMessage("hosts.restore")}
        logs={logs}
        metrics={metrics}
      />
    ),
    profiles: (
      <ProfilesTab
        favorites={favoriteProfiles}
        currentLevel={bypassLevel}
        currentTuning={bypassTuning}
        compatibility={compatibility}
        pending={controlsBusy}
        onSelect={handleProfileSelected}
        onLaunch={handleProfileLaunch}
        onFavoriteToggle={handleFavoriteToggle}
        onCompatibility={(mode, launch) => {
          if (controlsBusy) return;
          setCompatibility((current) => ({ ...current, mode, remembered: true }));
          postNativePayload({ type: "compatibility.set", mode, launch });
        }}
      />
    ),
    youtube: (
      <YouTubeTab
        ytActive={ytActive}
        dcActive={dcActive}
        ytSelected={ytSelected}
        dcSelected={dcSelected}
        onToggleYt={(value) => { if (!controlsBusy) { setYtSelected(value); postNativeMessage("service.set", value, "youtube"); } }}
        onToggleDc={(value) => { if (!controlsBusy) { setDcSelected(value); postNativeMessage("service.set", value, "discord"); } }}
        metrics={metrics}
        webBypass={webBypass}
        onToggleWeb={(value) => { if (!controlsBusy) { setWebBypass(value); postNativeMessage("service.set", value, "web"); } }}
      />
    ),
    telegram: (
      <TelegramTab
        tgActive={tgActive}
        tgSelected={tgSelected}
        webActive={telegramWebSelected}
        pending={controlsBusy}
        onToggleTg={(value) => { if (!controlsBusy) { setTgSelected(value); postNativeMessage("service.set", value, "telegram"); } }}
        onToggleWeb={(value) => { if (!controlsBusy) { setTelegramWebSelected(value); postNativeMessage("service.set", value, "telegramweb"); } }}
        onConfigureDesktop={() => postNativeMessage("telegram.proxy.configure")}
      />
    ),
    warp: (
      <WarpTab
        info={warp}
        onToggle={(value) => postNativeMessage("warp.toggle", value)}
        onInstall={() => postNativeMessage("warp.install")}
        onRefresh={() => postNativeMessage("warp.refresh")}
        onPerformance={() => postNativeMessage("warp.performance.refresh")}
        onOpen={() => postNativeMessage("warp.open")}
        onRestore={() => postNativeMessage("warp.restore")}
        onRetry={() => postNativeMessage("warp.retry")}
        onRegister={() => postNativeMessage("warp.register")}
        onCopyDiagnostics={() => postNativeMessage("warp.copyDiagnostics")}
        onSettings={(value) => { setWarp((current) => ({ ...current, ...value })); postNativePayload({ type: "warp.settings.update", ...value }); }}
        onOptimize={() => postNativeMessage("warp.optimize")}
        onProfileSave={(profile) => postNativePayload({ type: profile.id ? "warp.profile.update" : "warp.profile.create", profile })}
        onProfileToggle={(id, value) => postNativePayload({ type: "warp.profile.toggle", id, value })}
        onProfileDelete={(id) => postNativePayload({ type: "warp.profile.delete", id })}
      />
    ),
    gamefilter: (
      <GameFilterTab
        selected={gameFilterSelected}
        active={gameFilterActive}
        pending={controlsBusy}
        info={gameFilter}
        onToggle={(value) => { if (!controlsBusy) { setGameFilterSelected(value); postNativeMessage("service.set", value, "gamefilter"); } }}
        onChange={(value) => { setGameFilter(value); postNativePayload({ type: "gamefilter.set", selectedGames: value.selectedGames, protocol: value.protocol }); }}
      />
    ),
    settings: <SettingsTab update={update} domains={domains} settings={settings}
      onSettingsChange={(name, value) => {
        setSettings((current) => ({ ...current, [name]: value }));
        if (name === "autostart") postNativeMessage("settings.autostart", value as boolean);
        else if (name === "tray") postNativeMessage("settings.tray", value as boolean);
        else if (name === "startProtectionOnStartup") postNativeMessage("settings.startProtectionOnStartup", value as boolean);
        else if (name === "startupStrategy") postNativePayload({ type: "settings.startupStrategy", strategy: value });
        else if (name === "selectedDnsProvider") postNativePayload({ type: "settings.dnsProvider", provider: value });
      }}
      onSaveDomains={(value) => { setDomains(value); postNativePayload({ type: "domains.set", bypass: value.bypass, exclusions: value.exclusions }); }} />,
  };

  return (
    <div
      className="size-full flex overflow-hidden"
      style={{ fontFamily: "'Inter', sans-serif", background: "#09090F", minHeight: "100vh" }}
    >
      {/* Ambient glow */}
      <div
        className="fixed inset-0 pointer-events-none"
        aria-hidden
        style={{
          background: [
            "radial-gradient(ellipse 55% 60% at 8% 55%, rgba(108,92,231,0.1) 0%, transparent 70%)",
            "radial-gradient(ellipse 40% 45% at 92% 12%, rgba(0,120,212,0.08) 0%, transparent 70%)",
          ].join(","),
          transition: "opacity 0.5s",
          opacity: protection ? 1 : 0.4,
        }}
      />
      <UpdatePromptModal update={update} />

      {/* ══ SIDEBAR ══════════════════════════════════════════ */}
      <aside
        className="relative z-10 flex flex-col w-[236px] flex-shrink-0 h-screen"
        style={{
          background: "rgba(255,255,255,0.018)",
          backdropFilter: "blur(32px)",
          WebkitBackdropFilter: "blur(32px)",
          borderRight: "1px solid rgba(255,255,255,0.063)",
        }}
      >
        {/* Window chrome */}
        <div
          className="flex items-center gap-1.5 px-5 pt-4 pb-0 select-none"
          onMouseDown={() => postNativeMessage("window.drag")}
        >
          {["#FF5F56", "#FEBC2E", "#28C840"].map((bg, i) => (
            <button
              type="button"
              key={i}
              className="w-[11px] h-[11px] rounded-full cursor-pointer"
              style={{ background: bg, opacity: 0.75 }}
              aria-label={["Закрыть", "Свернуть", "Развернуть"][i]}
              onMouseDown={(event) => event.stopPropagation()}
              onClick={() => postNativeMessage(["window.close", "window.minimize", "window.maximize"][i])}
            />
          ))}
        </div>

        {/* Logo */}
        <div className="px-5 pt-5 pb-6">
          <div className="flex items-center gap-3">
            <motion.div
              whileHover={{ scale: 1.06, rotate: -2 }}
              transition={{ type: "spring", stiffness: 280, damping: 18 }}
              className="w-9 h-9 rounded-[10px] flex items-center justify-center flex-shrink-0"
              style={{
                background: "linear-gradient(145deg,rgba(108,18,34,0.96),rgba(29,10,16,0.98))",
                border: "1px solid rgba(229,66,91,0.5)",
                boxShadow: "0 0 18px rgba(207,32,61,0.25),0 5px 18px rgba(92,12,26,0.5)",
              }}
            >
              <img src="/vortexpass-logo.png" alt="VortexPass" className="w-[28px] h-[28px] object-contain" style={{ filter: "brightness(1.55) saturate(1.35) drop-shadow(0 0 5px rgba(225,46,73,0.45))" }} draggable={false} />
            </motion.div>
            <div>
              <div
                className="font-bold text-[13px] text-white leading-none"
                style={{ fontFamily: "'Plus Jakarta Sans', sans-serif" }}
              >
                VortexPass
              </div>
              <div className="text-[10px] font-mono text-[#777795] mt-0.5 tracking-[0.01em]">
                Network Manager
              </div>
            </div>
          </div>
        </div>

        {/* Quick status pill */}
        <div className="px-4 mb-4">
          <div
            className="flex items-center gap-2 px-3 py-2 rounded-xl transition-all duration-300"
            style={{
              background: protection
                ? "rgba(108,92,231,0.12)"
                : "rgba(255,255,255,0.04)",
              border: `1px solid ${protection ? "rgba(108,92,231,0.28)" : "rgba(255,255,255,0.07)"}`,
            }}
          >
            {protection ? (
              <ShieldCheck size={12} className="text-[#6C5CE7] flex-shrink-0" />
            ) : (
              <ShieldOff size={12} className="text-[#2A2A44] flex-shrink-0" />
            )}
            <span
              className="text-[11px] font-mono flex-1 transition-colors duration-300"
              style={{ color: protection ? "#8080C0" : "#2A2A44" }}
            >
              {protection ? `${running}/6 активно` : "Защита выкл."}
            </span>
            <Dot on={protection} pulse={protection} />
          </div>
        </div>

        {/* Nav */}
        <nav
          className="flex-1 px-3 space-y-0.5 overflow-y-auto"
          style={{ scrollbarWidth: "none" }}
        >
          <p className="text-[9px] font-mono uppercase tracking-[0.18em] text-[#1E1E38] px-3 mb-2">
            Навигация
          </p>
          {NAV.map((item) => {
            const active = tab === item.id;
            const NavIcon = item.Icon;
            const TrailingIcon = item.trailingIcon;
            return (
              <motion.button
                key={item.id}
                onClick={() => setTab(item.id)}
                whileHover={active ? {} : { x: 2 }}
                whileTap={{ scale: 0.985 }}
                className="w-full flex items-center gap-3 px-3 py-2.5 rounded-xl text-left relative"
              >
                {active && (
                  <motion.div
                    layoutId="nav-pill"
                    className="absolute inset-0 rounded-xl"
                    style={{
                      background: "rgba(108,92,231,0.15)",
                      border: "1px solid rgba(108,92,231,0.3)",
                    }}
                    transition={{ type: "spring", stiffness: 400, damping: 32 }}
                  />
                )}
                <span className="relative w-5 h-5 flex items-center justify-center leading-none">
                  <NavIcon
                    size={17}
                    style={{ color: active ? item.color : "#74779A", filter: active ? `drop-shadow(0 0 7px ${item.color}66)` : "none" }}
                  />
                </span>
                <div className="relative flex-1 min-w-0">
                  <div
                    className="text-[12.5px] font-semibold leading-none truncate transition-colors duration-150"
                    style={{
                      fontFamily: "'Plus Jakarta Sans', sans-serif",
                      color: active ? "#fff" : "#8A8DAD",
                    }}
                  >
                    {item.label}
                  </div>
                  <div
                    className="text-[10px] font-mono mt-0.5 truncate"
                    style={{ color: active ? "#A29AD7" : "#5D6080" }}
                  >
                    <span>{item.sub}</span>
                    {TrailingIcon && <TrailingIcon size={12} className="inline-block ml-1 align-[-2px]" style={{ color: active ? "#8790FF" : "#74779A" }} />}
                  </div>
                </div>
                {active && (
                  <ChevronRight
                    size={11}
                    className="relative text-[#A7A8CE] flex-shrink-0"
                  />
                )}
              </motion.button>
            );
          })}
        </nav>

        {/* Footer */}
        <div
          className="p-4 space-y-2.5 border-t"
          style={{ borderColor: "rgba(255,255,255,0.055)" }}
        >
          <div
            className="flex items-center gap-2 px-3 py-2 rounded-xl"
            style={{
              background: "rgba(0,214,143,0.07)",
              border: "1px solid rgba(0,214,143,0.18)",
            }}
          >
            <ShieldCheck size={11} className="text-[#00D68F] flex-shrink-0" />
            <span className="text-[10px] font-mono text-[#00D68F] font-semibold">
              Admin Rights: Active
            </span>
          </div>
          <div className="flex justify-between px-1">
            <span className="text-[10px] font-mono font-medium text-[#696A8D]">v{update.currentVersion ?? (window as NativeWindow).__vortexRuntime?.app?.version ?? "—"}</span>
            <span className="text-[10px] font-mono font-medium text-[#696A8D]">Windows x64</span>
          </div>
        </div>
      </aside>

      {/* ══ CONTENT ═════════════════════════════════════════ */}
      <div className="relative z-10 flex-1 flex flex-col min-w-0 h-screen">
        {/* Top bar */}
        <div
          className="flex items-center justify-between px-7 py-3 flex-shrink-0 select-none cursor-default"
          onMouseDown={() => postNativeMessage("window.drag")}
          onDoubleClick={() => postNativeMessage("window.maximize")}
          style={{
            background: "rgba(255,255,255,0.025)",
            borderBottom: "1px solid rgba(255,255,255,0.095)",
          }}
        >
          <div className="flex items-center gap-1.5 font-mono text-[11px]">
            <span className="text-[#6E7198]">VortexPass</span>
            <span className="text-[#50536F] mx-0.5">/</span>
            <span className="text-[#B1B3D2]">
              {NAV.find((n) => n.id === tab)?.label}
            </span>
          </div>

          <div className="flex items-center gap-3">
            <div
              className="flex items-center gap-1.5 px-2.5 py-1 rounded-lg"
              style={{
                background: "rgba(255,255,255,0.04)",
                border: "1px solid rgba(255,255,255,0.11)",
              }}
            >
              <Activity
                size={11}
                style={{ color: protection ? "#00D68F" : "#9094B7" }}
              />
              <span
                className="text-[10px] font-mono"
                style={{ color: protection ? "#00D68F" : "#9094B7" }}
              >
                {protection ? "Live" : "Idle"}
              </span>
            </div>

            <div
              className="w-px h-4"
              style={{ background: "rgba(255,255,255,0.14)" }}
            />

            <div className="flex items-center gap-0.5">
              {[
                { Icon: Minimize2, action: "window.minimize", label: "Свернуть" },
                { Icon: Square, action: "window.maximize", label: "Развернуть" },
                { Icon: X, action: "window.close", label: "Закрыть" },
              ].map(({ Icon, action, label }) => (
                <button
                  type="button"
                  key={action}
                  aria-label={label}
                  title={label}
                  onMouseDown={(event) => event.stopPropagation()}
                  onDoubleClick={(event) => event.stopPropagation()}
                  onClick={() => postNativeMessage(action)}
                  className="w-7 h-7 flex items-center justify-center rounded-lg transition-all duration-150 text-[#8D91B5] hover:bg-white/10 hover:text-[#F0F0FF]"
                >
                  <Icon size={11} />
                </button>
              ))}
            </div>
          </div>
        </div>

        {/* Scroll area */}
        <div
          className="flex-1 overflow-y-auto px-7 py-6"
          style={{ scrollbarWidth: "none" }}
        >
          <AnimatePresence mode="wait">
            <motion.div
              key={tab}
              initial={{ opacity: 0, y: 12 }}
              animate={{ opacity: 1, y: 0 }}
              exit={{ opacity: 0, y: -8, scale: 0.99 }}
              transition={{ duration: 0.2, ease: [0.32, 0.72, 0, 1] }}
            >
              {content[tab]}
            </motion.div>
          </AnimatePresence>
        </div>
      </div>
    </div>
  );
}
