import { normalizeDomains } from './profiles';
import type { Connection } from './siteRules';
export const WARP_STORAGE_KEY='vortexpass.design.warp.v1';
export type WarpPreferences={routing:'smart'|'full'|'proxy'|'dnsOnly';protocol:'auto'|'masque'|'wireguard';masque:'fallback'|'h3'|'h2';dns:'auto'|'udp'|'doh'|'dot'|'system';proxyPort:number};
export type WarpRoute={id:string;name:string;kind:'site'|'game';domains:string[];connection:Connection;ipRanges:string[];executable:string;enabled:boolean};
export type RouteDraft={id?:string;name:string;kind:'site'|'game';domainsText:string;connection:Connection;ipText:string;executable:string;enabled:boolean};
export const defaultPreferences:WarpPreferences={routing:'smart',protocol:'auto',masque:'fallback',dns:'auto',proxyPort:41870};
export const defaultRoute=():RouteDraft=>({name:'',kind:'site',domainsText:'',connection:'auto',ipText:'',executable:'',enabled:false});
export function routeErrors(draft:RouteDraft){
  const domainResult=normalizeDomains(draft.domainsText);const ranges=draft.ipText.split(/\r?\n/).map(s=>s.trim()).filter(Boolean);
  const validIp=(value:string)=>{const [ip,prefix,...extra]=value.split('/');const parts=ip.split('.');return !extra.length&&parts.length===4&&parts.every(n=>/^\d{1,3}$/.test(n)&&Number(n)<=255)&&(prefix===undefined||/^\d{1,2}$/.test(prefix)&&Number(prefix)<=32)};
  return {name:!draft.name.trim()||draft.name.trim().length>80?'Введите название (до 80 символов).':undefined,domains:domainResult.error,ip: ranges.some(r=>!validIp(r))?'Введите IPv4 или CIDR, например 192.0.2.0/24.':undefined,content:!domainResult.domains.length&&!(draft.kind==='game'&&ranges.length)?'Добавьте домен или, для игры, IP/CIDR.':undefined,connection:draft.kind==='game'&&draft.connection==='warpProxy'?'WARP-прокси не поддерживает игровой UDP.':undefined,executable:draft.executable.length>512?'Путь не длиннее 512 символов.':undefined};
}
export function createRoute(draft:RouteDraft):WarpRoute{return {id:draft.id??crypto.randomUUID(),name:draft.name.trim(),kind:draft.kind,domains:normalizeDomains(draft.domainsText).domains,connection:draft.connection,ipRanges:draft.kind==='game'?[...new Set(draft.ipText.split(/\r?\n/).map(s=>s.trim()).filter(Boolean))]:[],executable:draft.kind==='game'?draft.executable.trim():'',enabled:draft.enabled}}
export function loadWarp(){
  const fallback={preferences:{...defaultPreferences},routes:[] as WarpRoute[],warning:''};
  try{const raw=localStorage.getItem(WARP_STORAGE_KEY);if(!raw)return fallback;const p=JSON.parse(raw);const settings=p.preferences;
    if(p.version!==1||!settings||!['smart','full','proxy','dnsOnly'].includes(settings.routing)||!['auto','masque','wireguard'].includes(settings.protocol)||!['fallback','h3','h2'].includes(settings.masque)||!['auto','udp','doh','dot','system'].includes(settings.dns)||!Number.isInteger(settings.proxyPort)||settings.proxyPort<1||settings.proxyPort>65535||!Array.isArray(p.routes))throw new Error();
    const routes:WarpRoute[]=[];let invalid=false;
    for(const r of p.routes){try{if(!r||typeof r.id!=='string'||!r.id||routes.some(old=>old.id===r.id)||typeof r.name!=='string'||!['site','game'].includes(r.kind)||!['auto','direct','warpProxy','warpSystem'].includes(r.connection)||!Array.isArray(r.domains)||r.domains.some((x:unknown)=>typeof x!=='string')||!Array.isArray(r.ipRanges)||r.ipRanges.some((x:unknown)=>typeof x!=='string')||typeof r.executable!=='string'||typeof r.enabled!=='boolean')throw new Error();const draft={...r,domainsText:r.domains.join('\n'),ipText:r.ipRanges.join('\n')};if(Object.values(routeErrors(draft)).some(Boolean))throw new Error();routes.push(createRoute(draft))}catch{invalid=true}}
    return {preferences:settings as WarpPreferences,routes,warning:invalid?'Некоторые сохранённые маршруты повреждены и не показаны.':''};
  }catch{return {...fallback,warning:'Не удалось загрузить настройки WARP. Новые изменения попробуем сохранить; сетевые параметры не меняются.'}}
}
