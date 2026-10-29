import { useState } from "react";
import { postNativeMessage, postNativePayload, useNativeMessage } from "./NativeBridge";
import {mergeYoutubeStatus,type YoutubeAdvisorInfo} from './YoutubeAdvisorState';
export type Tab = "home" | "profiles" | "warp" | "activity" | "settings";
export const warpTransportNames:Record<string,string>={auto:"Авто","masque-h2":"HTTP/2","masque-h3-fallback":"HTTP/3 + fallback","masque-h3":"HTTP/3",wireguard:"WireGuard"};

type WarpProbeState = "available" | "restricted" | "partial" | "serviceError" | "unavailable" | "timedOut" | "routeUnconfirmed" | "availabilityUnknown" | "regionRestricted";
type WarpEndpointDiagnostic = { label: string; state: WarpProbeState; detail: string; httpStatus?: number; latencyMs?: number };
type WarpScenarioDiagnostic = { state: WarpProbeState; summary: string; endpoints: Record<string, WarpEndpointDiagnostic> };
type WarpPerformanceEndpoint = { label: string; available: boolean; medianLatencyMs?: number; jitterMs?: number; detail: string };
type WarpPerformance = { warpConnected: boolean; checkedAt: string; summary: string; verdict?: string; recommendation?: string; endpoints: Record<string, WarpPerformanceEndpoint> };
type WarpRouteProfile = { id: string; name: string; kind: "site" | "game"; builtInId?: string; enabled: boolean; connection: "auto" | "direct" | "warpProxy" | "warpSystem"; domains: string[]; ipRanges: string[]; executablePath?: string };

type WarpInfo = {
  routeConfirmed?: boolean;
  activeTransport?: {protocol:string;masque:string;bootstrap:string};
  observedTransport?: string;
  diagnosticDetails?: boolean;
  proxyMetrics?: {enabled:boolean;activeTunnels:number;completed:number;failures:number;rejected:number;sentBytes:number;receivedBytes:number;handshakes:number;meanHandshakeMs?:number};
  mediaRunning?: boolean;
  mediaProgress?: string;
  mediaComparison?: {checkedAt:string;summary:string;samples:{route:string;round:number;bytes:number;durationMs:number;complete:boolean;state:string;mbps?:number}[]};
  youtubeComparison?: string;
  youtubeComparing?: boolean;
  guiSuppressed?: boolean;
  guiStartupCanRestore?: boolean;
  setupChecked?: boolean;
  preparationCompleted?: boolean;
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
  transportSelection?: "auto" | "masque-h2" | "masque-h3-fallback" | "masque-h3" | "wireguard";
  transportSelectionSource?: string;
  transportCandidates?: Array<{protocol:string;masque:string;bootstrap:string}>;
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
  dnsMode?: "system" | "google-udp" | "doh";
    startProtectionOnStartup?: boolean;
    startupConnectionError?: string; startupConnectionId?: string;
    startupProfileChoiceRequired?: boolean;
    startupStrategy?: "auto" | "light" | "moderate" | "medium" | "complex" | "aggressive";
    gameFilterIpsetMode?: "any" | "loaded" | "none";
  };
  __vortexRuntime?: Record<string, any>;
};


function nativeSetting<K extends keyof NonNullable<NativeWindow["__vortexSettings"]>>(
  name: K,
  fallback: NonNullable<NativeWindow["__vortexSettings"]>[K]
) {
  return ((window as NativeWindow).__vortexSettings?.[name] ?? fallback) as NonNullable<NativeWindow["__vortexSettings"]>[K];
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

interface HealthItem { state: string; detail: string; latencyMs: number | null; measurementType?: "routeprobe" | "voiceprobe" | "gamesession"; stages?: Record<string, string | null>; systemDnsReady?: boolean; groups?: { id: string; state: string; detail: string }[] }
type HealthMap = Record<string, HealthItem>;
interface HostsInfo { applied: boolean; entryCount: number; updatedAt?: string; detail: string; sourceCommit?: string; telegramFallbackCount?: number; telegramDnsCount?: number; robloxCdnCount?: number }
interface GameFilterInfo { selectedGames: string[]; protocol: "udp" | "tcp" | "all"; ipsetMode?: "any" | "loaded" | "none" }
interface DomainListsInfo { bypass: string[]; exclusions: string[] }
interface SettingsInfo {
  theme?: string;
  reduceMotion?: boolean;
  autostart: boolean;
  tray: boolean;
  selectedDnsProvider?: "auto" | "google" | "cloudflare" | "quad9";
  dnsMode?: "system" | "google-udp" | "doh";
  startProtectionOnStartup?: boolean;
  startupConnectionError?: string; startupConnectionId?: string;
    startupProfileChoiceRequired?: boolean;
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
interface AdvisorCandidate { id?: string; level: string; tuning: number; mode?: "standard" | "mobile"; label: string; healthy: number; total: number; latencyMs: number | null; jitterMs?: number | null; fullyConfirmed?: boolean; summary: string; score?: number; source?: string; services?: { service: string; confirmed: boolean; detail: string; groups?: {id:string;state:string;detail:string}[] }[] }
interface AdvisorModelInfo { provider: string; providers: string[]; model: { ready: boolean; name: string; detail: string } }
interface AdvisorInfo {
  phase?:number; elapsedMs?:number; remainingMs?:number;
  state: "idle" | "detecting" | "testing" | "restoring" | "complete" | "failed";
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


export interface ConnectionProfile { telegramWebRoute?: "direct" | "warpProxy"; robloxAssetsMode?: "auto" | "standard" | "enhanced"; id: string; name: string; provider?: string; services: string[]; games: string[]; domains: string[]; level: string; tuning: number; mode: "standard" | "mobile"; favorite: boolean; updatedAt: string; siteRules?: import("./design/SiteRulesEditor").SiteRule[]; hasStrategy?: boolean; routeNeedsCheck?: boolean }
export interface ConnectionsInfo { profiles: ConnectionProfile[]; selectedId: string; active: ConnectionProfile | null; change?: string; changedId?: string }
export const levels = ["light", "moderate", "medium", "complex", "aggressive"];
export const levelNames = ["Мягкий", "Сбалансированный", "Усиленный", "Активный", "Максимальный"];
export const serviceNames: Record<string,string> = { youtube:"YouTube", discord:"Discord", telegram:"Telegram", telegramweb:"Telegram Web", telegramdesktop:"Telegram Desktop", web:"Web", gamefilter:"Game Filter" };
export const gameNames: Record<string,string> = {roblox:"Roblox", phasmophobia:"Phasmophobia", deadbydaylight:"Dead by Daylight"};

function nowTs(){return new Date().toTimeString().slice(0,8);}
export function useNativeApp() {
  const [tab, setTab] = useState<Tab>("home");
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
  const [connections, setConnections] = useState<ConnectionsInfo>({ profiles: [], selectedId: "", active: null });
  const [health, setHealth] = useState<HealthMap>({});
  const [hosts, setHosts] = useState<HostsInfo>({ applied: false, entryCount: 0, detail: "hosts не применён" });
  const [logs, setLogs] = useState<LogEntry[]>([]);
  const [metrics, setMetrics] = useState<Metrics>({ uptimeSeconds: 0, trafficBytes: 0, packets: 0, youtubePing: null, discordPing: null, telegramPing: null });
  const [update, setUpdate] = useState<UpdateInfo>({ state: "idle", currentVersion: (window as NativeWindow).__vortexRuntime?.app?.version });
  const [settings, setSettings] = useState<SettingsInfo>({
    theme: (window as NativeWindow).__vortexRuntime?.settings?.theme ?? "dark",
    reduceMotion: false,
    autostart: Boolean(nativeSetting("autostart", false)),
    tray: Boolean(nativeSetting("tray", true)),
    dnsMode: nativeSetting("dnsMode", "google-udp"),
    selectedDnsProvider: nativeSetting("selectedDnsProvider", "auto"),
    startProtectionOnStartup: nativeSetting("startProtectionOnStartup", false),
    startupConnectionId: nativeSetting("startupConnectionId", ""),
    startupProfileChoiceRequired: nativeSetting("startupProfileChoiceRequired", false),
    startupConnectionError: nativeSetting("startupConnectionError", undefined),
    startupStrategy: nativeSetting("startupStrategy", "auto"),
    gameFilterIpsetMode: nativeSetting("gameFilterIpsetMode", "any"),
  });
  const [youtubeAdvisor,setYoutubeAdvisor]=useState<YoutubeAdvisorInfo>({state:'idle'});
  const [connectionNotices,setConnectionNotices]=useState<{time:string;level:string;message:string;stage:string;generation:number}[]>([]);
  const startYoutubeRun=(profileId:string,searchMode:'normal'|'extended'='normal')=>{
    const runId=crypto.randomUUID();setYoutubeAdvisor({state:'testing',runId,profileId,revision:0,message:'Начинаем отдельную проверку YouTube',readiness:'busy'});
    postNativePayload({type:'advisor.run',scope:'youtube',profileId,runId,searchMode});
  };  const [advisor, setAdvisor] = useState<AdvisorInfo>({ state: "idle" });
  const [advisorModel, setAdvisorModel] = useState<AdvisorModelInfo>({ provider: "", providers: [], model: { ready: false, name: "Локальная модель", detail: "Проверяем комплект модели…" } });
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
    transportSelection: (window as NativeWindow).__vortexRuntime?.warp?.transportSelection ?? "auto",
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

  const [warpPreparation,setWarpPreparation]=useState<{state:string;detail?:string}>({state:"idle"});
  const [dnsReport,setDnsReport]=useState<{state:string;draftId?:string;error?:string;choices?:{id:string;mode:string;provider:string;working:boolean;missingHosts:string[]}[];results?:{provider:string;host:string;recordType:string;state:string;durationMs:number;detail:string}[]}>({state:"idle"});
  const [dnsProblem,setDnsProblem]=useState<{draftId?:string;missing?:string[];blockedServices?:string[];canContinue?:boolean}>({});
  const [robloxDiagnostic,setRobloxDiagnostic]=useState<{state:string;detail?:string;hosts?:string[];resources?:{host:string;kind:string;state:string;detail:string}[]}>({state:"idle"});
  useNativeMessage((message) => {
    if(message?.type==="youtube.advisor.status")setYoutubeAdvisor(current=>mergeYoutubeStatus(current,message));
    if (!message?.type) return;
    if(message.type==="advisor.dns.status")setDnsReport(message);
    if(message.type==="warp.preparation")setWarpPreparation(message);
    if(message.type==="warp.preflight")setWarp(current=>({...current,detail:message.message,stage:message.stage}));
    if(message.type==="advisor.dns.problem")setDnsProblem(message);
    if(message.type==="roblox.diagnostic")setRobloxDiagnostic(message);
    if (message.type === "connections.status") setConnections(message as ConnectionsInfo);
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
        theme: message.theme ?? current.theme ?? "dark",
        reduceMotion: Boolean(message.reduceMotion ?? current.reduceMotion),
        autostart: Boolean(message.autostart),
        tray: Boolean(message.tray),
        dnsMode: message.dnsMode ?? current.dnsMode ?? "google-udp",
        selectedDnsProvider: message.selectedDnsProvider ?? current.selectedDnsProvider ?? "auto",
        startProtectionOnStartup: Boolean(message.startProtectionOnStartup ?? current.startProtectionOnStartup),
        startupConnectionError: message.startupConnectionError, startupConnectionId: message.startupConnectionId ?? current.startupConnectionId, startupProfileChoiceRequired: message.startupProfileChoiceRequired ?? current.startupProfileChoiceRequired, startupStrategy: message.startupStrategy ?? current.startupStrategy ?? "auto",
        gameFilterIpsetMode: message.gameFilterIpsetMode ?? current.gameFilterIpsetMode ?? "any",
      }));
    }
    if (message.type === "advisor.local.status") setAdvisor(message as AdvisorInfo);
    if (message.type === "advisor.model.status") setAdvisorModel(message as AdvisorModelInfo);
    if (message.type === "warp.status") setWarp(message as WarpInfo);
    if (message.type === "warp.progress") setWarp((current) => ({ ...current, progress: Number(message.progress ?? 0), pending: true }));
    if (message.type === "configuration.status") {
      if (message.gameFilter) setGameFilter(message.gameFilter as GameFilterInfo);
      if (message.domains) setDomains(message.domains as DomainListsInfo);
    }
    if (message.type === "protection.status") { if(Array.isArray(message.connectionNotices))setConnectionNotices(message.connectionNotices);
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
  const advisorBusy = advisor.state === "detecting" || advisor.state === "testing" || advisor.state === "restoring";
  const controlsBusy = protectionPending || advisorBusy || warp.pending || !["idle","ready","failed"].includes(warpPreparation.state);

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

  return { youtubeAdvisor,startYoutubeRun,connectionNotices,warpPreparation,dnsReport,dnsProblem,robloxDiagnostic, tab, setTab, protection, protectionPending, ytActive, dcActive, tgActive, gameFilterActive, ytSelected, dcSelected, tgSelected, telegramWebSelected, webBypass, gameFilterSelected, gameFilter, setGameFilter, domains, setDomains, bypassLevel, bypassTuning, compatibility, favoriteProfiles, health, hosts, logs, metrics, update, settings, setSettings, advisor, advisorModel, setAdvisorModel, warp, connections, controlsBusy, advisorBusy, handleToggleProtection, handleProfileSelected, handleFavoriteToggle, handleProfileLaunch };
}
export type NativeState = ReturnType<typeof useNativeApp>;



