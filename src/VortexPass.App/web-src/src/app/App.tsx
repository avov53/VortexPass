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
} from "lucide-react";

// ─── Types ────────────────────────────────────────────────────────────────────

type Tab = "dashboard" | "profiles" | "youtube" | "telegram" | "gamefilter" | "settings";

type NativeWebView = { postMessage: (message: unknown) => void };
type NativeWindow = Window & {
  chrome?: { webview?: NativeWebView };
  __vortexSettings?: { autostart?: boolean; tray?: boolean };
  __vortexRuntime?: Record<string, any>;
};

function postNativeMessage(type: string, value?: boolean, serviceId?: string) {
  (window as NativeWindow).chrome?.webview?.postMessage({ type, value, serviceId });
}

function postNativePayload(payload: Record<string, unknown>) {
  (window as NativeWindow).chrome?.webview?.postMessage(payload);
}

function nativeSetting(name: "autostart" | "tray", fallback: boolean) {
  return (window as NativeWindow).__vortexSettings?.[name] ?? fallback;
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
  state: "idle" | "checking" | "current" | "available" | "failed" | "downloading";
  currentVersion?: string;
  availableVersion?: string;
  checkedAt?: string;
  error?: string;
}

interface HealthItem { state: string; detail: string; latencyMs: number | null; stages?: Record<string, string | null> }
type HealthMap = Record<string, HealthItem>;
interface HostsInfo { applied: boolean; entryCount: number; updatedAt?: string; detail: string; sourceCommit?: string }
interface GameFilterInfo { selectedGames: string[]; protocol: "udp" | "tcp" | "all" }
interface DomainListsInfo { bypass: string[]; exclusions: string[] }
interface SettingsInfo { autostart: boolean; tray: boolean }
interface FavoriteProfile { level: string; tuning: number }
interface AdvisorCandidate { level: string; tuning: number; label: string; healthy: number; total: number; latencyMs: number | null; summary: string }
interface AdvisorInfo {
  state: "idle" | "detecting" | "testing" | "complete" | "failed";
  provider?: string;
  level?: string;
  progress?: number;
  total?: number;
  message?: string;
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

const PROFILE_CATALOG = PROFILE_LEVELS.flatMap((level) =>
  PROFILE_TUNINGS.map((tuning) => ({
    level: level.id,
    tuning: tuning.id,
    label: `${level.label} · ${tuning.label}`,
    levelLabel: level.label,
    tuningLabel: tuning.label,
    short: level.short,
    description: `${level.description} ${tuning.detail}.`,
  })),
);

function profileKey(profile: FavoriteProfile) {
  return `${profile.level}:${profile.tuning}`;
}

function profileCatalogItem(profile: FavoriteProfile) {
  return PROFILE_CATALOG.find((item) => item.level === profile.level && item.tuning === profile.tuning) ?? PROFILE_CATALOG[8];
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
      className={`rounded-2xl transition-all duration-200 ${className}`}
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
    <div ref={ref} className="relative">
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
            className="absolute z-50 w-full mt-1.5 rounded-xl overflow-hidden"
            style={{
              background: "rgba(18,18,28,0.96)",
              backdropFilter: "blur(24px)",
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
  favoriteProfiles,
  health,
  hosts,
  advisor,
  onRunAdvisor,
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
  favoriteProfiles: FavoriteProfile[];
  health: HealthMap;
  hosts: HostsInfo;
  advisor: AdvisorInfo;
  onRunAdvisor: () => void;
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
      active: health.youtube?.state === "healthy",
      iconColor: "#FF4D6D",
      ping: health.youtube?.latencyMs != null ? `${health.youtube.latencyMs} ms` : "—",
      sub: health.youtube?.detail ?? (ytActive ? "Проверяем сеть" : "Не выбран"),
      subColor: health.youtube?.state === "healthy" ? "#00D68F" : "#FFB340",
    },
    {
      id: "dc",
      label: "Discord",
      Icon: DiscordIcon,
      active: health.discord?.state === "healthy",
      iconColor: "#5865F2",
      ping: health.discord?.latencyMs != null ? `${health.discord.latencyMs} ms` : "—",
      sub: health.discord?.detail ?? (dcActive ? "Проверяем сеть" : "Не выбран"),
      subColor: health.discord?.state === "healthy" ? "#00D68F" : "#FF4D6D",
    },
    {
      id: "tg",
      label: "Telegram",
      Icon: TelegramIcon,
      active: health.telegram?.state === "healthy",
      iconColor: "#29B6F6",
      ping: health.telegram?.latencyMs != null ? `${health.telegram.latencyMs} ms` : "—",
      sub: health.telegram?.detail ?? (tgActive ? "Проверяем сеть" : "Не выбран"),
      subColor: health.telegram?.state === "healthy" ? "#00D68F" : "#FF4D6D",
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
            whileHover={pending ? {} : { scale: 1.04 }}
            whileTap={pending ? {} : { scale: 0.95 }}
            onClick={onToggleProtection}
            disabled={pending}
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
               {pending ? "Запуск защиты…" : protection ? "Защита активна" : "Запустить защиту"}
            </h2>
            <p className="text-[12px] font-mono mt-1" style={{ color: protection ? "#8080C0" : "#2A2A44" }}>
              {pending
                ? "Проверяем файлы и запускаем winws"
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
                Определит провайдера и проверит 20 внутренних стратегий на выбранных сервисах. Пять понятных уровней на дашборде останутся без изменений.
              </div>
              <div className="text-[9px] font-mono text-[#3A3A5E] mt-1.5">Проверка может занять несколько минут. Выполняется один HTTPS-запрос к ipapi.co; публичный IP в VortexPass не сохраняется.</div>
            </div>
          </div>
          <motion.button whileHover={advisor.state === "detecting" || advisor.state === "testing" ? {} : { scale: 1.02 }} whileTap={{ scale: 0.98 }}
            type="button" onClick={onRunAdvisor} disabled={pending || advisor.state === "detecting" || advisor.state === "testing"}
            className="px-4 py-2.5 rounded-xl text-[11px] font-semibold whitespace-nowrap disabled:opacity-50"
            style={{ color: "#C2BCFF", background: "rgba(108,92,231,0.14)", border: "1px solid rgba(108,92,231,0.32)" }}>
            {advisor.state === "detecting" || advisor.state === "testing" ? "Идёт диагностика…" : "Подобрать профиль"}
          </motion.button>
        </div>

        {(advisor.state === "detecting" || advisor.state === "testing") && (
          <motion.div initial={{ opacity: 0, y: 4 }} animate={{ opacity: 1, y: 0 }} className="relative mt-4">
            <div className="flex justify-between text-[10px] font-mono mb-2">
              <span className="text-[#7777A8]">{advisor.message ?? "Проверяем сеть…"}</span>
              <span className="text-[#4A4A6A]">{advisor.provider ?? "Провайдер определяется"}</span>
            </div>
            <div className="h-1.5 rounded-full overflow-hidden bg-white/[0.04]">
              <motion.div className="h-full rounded-full" animate={{ width: `${advisor.state === "detecting" ? 5 : Math.max(5, (((advisor.progress ?? 0) + 1) / (advisor.total ?? 20)) * 100)}%` }}
                style={{ background: "linear-gradient(90deg, #6C5CE7, #0078D4)" }} />
            </div>
          </motion.div>
        )}

        {advisor.state === "failed" && <div className="relative mt-4 text-[10px] font-mono text-[#FF7A8F]">{advisor.error}</div>}

        {advisor.state === "complete" && (
          <motion.div initial={{ opacity: 0, y: 6 }} animate={{ opacity: 1, y: 0 }} className="relative mt-4">
            <div className="flex items-center justify-between mb-2">
              <span className="text-[10px] font-mono text-[#7474A0]">Лучшие варианты{advisor.provider ? ` · ${advisor.provider}` : ""}</span>
              <span className="text-[9px] font-mono text-[#3A3A5E]">Применение только вручную</span>
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
                      <div className="text-[9px] font-mono text-[#3A3A5E] mt-1">{candidate.latencyMs != null ? `Среднее ${candidate.latencyMs} мс` : "Без замера задержки"}</div>
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
                {hosts.applied ? `Применён · ${hosts.entryCount} записей · Flowseal ${hosts.sourceCommit?.slice(0, 7) ?? ""}` : hosts.detail || "Не применён"}
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
              const selected = bypassLevel === profile.level && bypassTuning === profile.tuning;
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

      {dcSelected && health.discord && health.discord.state !== "healthy" && (
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
  pending,
}: {
  tgActive: boolean;
  tgSelected: boolean;
  webActive: boolean;
  onToggleTg: (v: boolean) => void;
  onToggleWeb: (v: boolean) => void;
  pending: boolean;
}) {
  const tgDesktop = tgSelected;
  const tgWeb = webActive;
  const applying = false;
  const applied = tgActive;

  const handleApply = () => onToggleTg(true);

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
                  Обрабатывается системным DPI-движком при включённой защите
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
                { label: "Движок", value: "winws" },
                { label: "Драйвер", value: "WinDivert" },
                { label: "Режим", value: "System" },
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
                ? "Защита Telegram активна"
                : "Включить защиту Telegram"}
            </motion.button>

            {applied && (
              <motion.p
                initial={{ opacity: 0, y: -4 }}
                animate={{ opacity: 1, y: 0 }}
                className="text-center text-[10px] font-mono text-[#3A3A5E] mt-2"
              >
                Трафик Telegram обрабатывается системным DPI-движком.
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
// TAB 4 — GAME FILTER
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
    { id: "phasmophobia", name: "Phasmophobia", subtitle: "Photon · игровые соединения", color: "#F7B955" },
    { id: "roblox", name: "Roblox", subtitle: "Изображения, аватары и CDN", color: "#6AA8FF" },
  ] as const;
  const selectedGames = games.filter((game) => info.selectedGames.includes(game.id));
  const setGame = (id: "phasmophobia" | "roblox", value: boolean) => {
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
            {active ? `Активен · ${info.protocol === "all" ? "UDP + TCP" : info.protocol.toUpperCase()}` : "Применяется только после главной кнопки запуска"}
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
                      {game.id === "roblox" ? <span className="text-[13px] font-black leading-none">R</span> : <GameFilterIcon size={18} />}
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
            ["udp", "UDP", "Быстрый игровой трафик"],
            ["tcp", "TCP", "Надёжные соединения"],
            ["all", "UDP + TCP", "По умолчанию"],
          ].map(([id, label, sub]) => (
            <button key={id} type="button" onClick={() => onChange({ ...info, protocol: id as GameFilterInfo["protocol"] })}
              className="px-3 py-3 rounded-xl text-left transition-all"
              style={{ background: info.protocol === id ? "rgba(247,185,85,0.12)" : "rgba(255,255,255,0.035)", border: `1px solid ${info.protocol === id ? "rgba(247,185,85,0.32)" : "rgba(255,255,255,0.06)"}` }}>
              <div className="text-[12px] font-semibold" style={{ color: info.protocol === id ? "#FFD28A" : "#666684" }}>{label}</div>
              <div className="text-[9px] font-mono text-[#3A3A5E] mt-1">{sub}</div>
            </button>
          ))}
        </div>
        <p className="text-[10px] font-mono text-[#4A4A6A] mt-4">Изменения сохранены и будут применены при следующем запуске защиты. Winws не перезапускается автоматически.</p>
      </GlassCard>
    </div>
  );
}

// ═══════════════════════════════════════════════════════════════════════════════
// TAB 5 — PROFILES
// ═══════════════════════════════════════════════════════════════════════════════

function ProfilesTab({ favorites, currentLevel, currentTuning, pending, onSelect, onLaunch, onFavoriteToggle }: {
  favorites: FavoriteProfile[];
  currentLevel: string;
  currentTuning: number;
  pending: boolean;
  onSelect: (profile: FavoriteProfile) => void;
  onLaunch: (profile: FavoriteProfile) => void;
  onFavoriteToggle: (profile: FavoriteProfile, favorite: boolean) => void;
}) {
  const [query, setQuery] = useState("");
  const normalizedQuery = query.trim().toLocaleLowerCase("ru-RU");
  const profiles = PROFILE_CATALOG.filter((profile) =>
    !normalizedQuery || [profile.label, profile.short, profile.description, profile.level]
      .some((value) => value.toLocaleLowerCase("ru-RU").includes(normalizedQuery)),
  );

  return (
    <div className="flex flex-col gap-5">
      <div>
        <h1 className="text-[23px] font-bold text-white" style={{ fontFamily: "'Plus Jakarta Sans', sans-serif" }}>Профили</h1>
        <p className="text-[12px] text-[#67678B] mt-1">Все 20 стратегий обхода, быстрый запуск и избранное для дашборда</p>
      </div>

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
          <motion.div initial={{ opacity: 0, height: 0 }} animate={{ opacity: 1, height: "auto" }} exit={{ opacity: 0, height: 0 }} className="overflow-hidden">
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

function SettingsTab({ update, domains, settings, onSettingsChange, onSaveDomains }: { update: UpdateInfo; domains: DomainListsInfo; settings: SettingsInfo; onSettingsChange: (name: keyof SettingsInfo, value: boolean) => void; onSaveDomains: (value: DomainListsInfo) => void }) {
  const handleUpdate = () => postNativeMessage(update.state === "available" ? "update.install" : "update.check");

  const updateMeta = {
    idle: { label: "Проверить обновления GitHub", color: "#B5AEFC", bg: "rgba(108,92,231,0.15)", border: "rgba(108,92,231,0.3)" },
    checking: { label: "Проверка обновлений GitHub…", color: "#B5AEFC", bg: "rgba(108,92,231,0.15)", border: "rgba(108,92,231,0.3)" },
    current: { label: "У вас актуальная версия", color: "#00D68F", bg: "rgba(0,214,143,0.1)", border: "rgba(0,214,143,0.3)" },
    available: { label: `Скачать v${update.availableVersion ?? ""} с GitHub`, color: "#FFB340", bg: "rgba(255,179,64,0.1)", border: "rgba(255,179,64,0.3)" },
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
          Поведение приложения и обновление VortexPass
        </p>
      </div>

      {/* Behaviour */}
      <GlassCard className="p-5">
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
              Добавляет VortexPass в автозагрузку через реестр HKCU\Run
            </div>
          </div>
          <Toggle checked={settings.autostart} onChange={(value) => onSettingsChange("autostart", value)} />
        </div>

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

      {/* Application updates */}
      <GlassCard className="p-5">
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
          whileHover={update.state === "checking" || update.state === "downloading" ? {} : { scale: 1.015, y: -1 }}
          whileTap={update.state === "checking" || update.state === "downloading" ? {} : { scale: 0.98 }}
          onClick={handleUpdate}
          disabled={update.state === "checking" || update.state === "downloading" || update.state === "current"}
          className="w-full flex items-center justify-center gap-2.5 py-3 rounded-xl font-semibold text-sm transition-all duration-300"
          style={{
            fontFamily: "'Plus Jakarta Sans', sans-serif",
            background: meta.bg,
            color: meta.color,
            border: `1px solid ${meta.border}`,
            boxShadow:
              update.state === "idle" || update.state === "available"
                ? "0 4px 16px rgba(108,92,231,0.2)"
                : "none",
          }}
        >
          {update.state === "checking" || update.state === "downloading" ? (
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
      <GlassCard className="p-5">
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
  { id: "youtube", Icon: YouTubeIcon, trailingIcon: DiscordIcon, label: "YouTube & Discord", sub: "Стратегии обхода DPI", color: "#FF5B72" },
  { id: "telegram", Icon: TelegramIcon, label: "Telegram", sub: "Desktop · Web", color: "#45C5F5" },
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
  const [favoriteProfiles, setFavoriteProfiles] = useState<FavoriteProfile[]>(() => {
    const profiles = (window as NativeWindow).__vortexRuntime?.favoriteProfiles;
    return Array.isArray(profiles) ? profiles.slice(0, 5).map((profile) => ({ level: String(profile.level), tuning: Number(profile.tuning ?? 0) })) : [];
  });
  const [health, setHealth] = useState<HealthMap>({});
  const [hosts, setHosts] = useState<HostsInfo>({ applied: false, entryCount: 0, detail: "hosts не применён" });
  const [logs, setLogs] = useState<LogEntry[]>(SEED_LOGS);
  const [metrics, setMetrics] = useState<Metrics>({ uptimeSeconds: 0, trafficBytes: 0, packets: 0, youtubePing: null, discordPing: null, telegramPing: null });
  const [update, setUpdate] = useState<UpdateInfo>({ state: "idle", currentVersion: (window as NativeWindow).__vortexRuntime?.app?.version });
  const [settings, setSettings] = useState<SettingsInfo>({ autostart: nativeSetting("autostart", false), tray: nativeSetting("tray", true) });
  const [advisor, setAdvisor] = useState<AdvisorInfo>({ state: "idle" });

  useNativeMessage((message) => {
    if (!message?.type) return;
    if (message.type === "metrics") setMetrics(message as Metrics);
    if (message.type === "update.status") setUpdate(message as UpdateInfo);
    if (message.type === "profile.status") {
      setBypassLevel(String(message.level ?? "medium"));
      setBypassTuning(Number(message.tuning ?? 0));
      if (Array.isArray(message.favoriteProfiles)) {
        setFavoriteProfiles(message.favoriteProfiles.slice(0, 5).map((profile: any) => ({ level: String(profile.level), tuning: Number(profile.tuning ?? 0) })));
      }
    }
    if (message.type === "health.status") setHealth((message.services ?? {}) as HealthMap);
    if (message.type === "hosts.status") setHosts(message as HostsInfo);
    if (message.type === "settings.status") setSettings({ autostart: Boolean(message.autostart), tray: Boolean(message.tray) });
    if (message.type === "advisor.status") setAdvisor(message as AdvisorInfo);
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
      setWebBypass(Boolean(message.requestedWeb ?? message.web));
      setGameFilterSelected(Boolean(message.requestedGameFilter));
    }
    if (message.type === "log") {
      setLogs((current) => [...current.slice(-249), { ts: nowTs(), level: message.level ?? "info", msg: message.message }]);
    }
  });

  const running = [ytActive, dcActive, tgActive, protection && webBypass, gameFilterActive].filter(Boolean).length;
  const advisorBusy = advisor.state === "detecting" || advisor.state === "testing";
  const controlsBusy = protectionPending || advisorBusy;

  // Sync: master protection toggle controls all services
  const handleToggleProtection = () => {
    if (controlsBusy) return;
    const next = !protection;
    postNativeMessage("protection.set", next);
  };

  const handleProfileSelected = (profile: FavoriteProfile) => {
    if (controlsBusy) return;
    setBypassLevel(profile.level);
    setBypassTuning(profile.tuning);
    postNativePayload({ type: "profile.set", level: profile.level, tuning: profile.tuning });
  };

  const handleFavoriteToggle = (profile: FavoriteProfile, favorite: boolean) => {
    if (favorite && favoriteProfiles.length >= 5) return;
    postNativePayload({ type: "profile.favorite.set", level: profile.level, tuning: profile.tuning, favorite });
  };

  const handleProfileLaunch = (profile: FavoriteProfile) => {
    if (controlsBusy) return;
    setBypassLevel(profile.level);
    setBypassTuning(profile.tuning);
    setProtectionPending(true);
    postNativePayload({ type: "profile.launch", level: profile.level, tuning: profile.tuning });
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
        favoriteProfiles={favoriteProfiles}
        health={health}
        hosts={hosts}
        advisor={advisor}
        onRunAdvisor={() => postNativePayload({ type: "advisor.run" })}
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
        pending={controlsBusy}
        onSelect={handleProfileSelected}
        onLaunch={handleProfileLaunch}
        onFavoriteToggle={handleFavoriteToggle}
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
        webActive={webBypass}
        pending={controlsBusy}
        onToggleTg={(value) => { if (!controlsBusy) { setTgSelected(value); postNativeMessage("service.set", value, "telegram"); } }}
        onToggleWeb={(value) => { if (!controlsBusy) { setWebBypass(value); postNativeMessage("service.set", value, "web"); } }}
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
        postNativeMessage(name === "autostart" ? "settings.autostart" : "settings.tray", value);
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
              {protection ? `${running}/5 активно` : "Защита выкл."}
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
