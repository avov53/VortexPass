export const componentOptions=[['page','Страница'],['images','Изображения'],['video','Видео'],['voice','Голосовая связь'],['game','Игровой трафик']] as const;
export type ComponentKind=typeof componentOptions[number][0];
export type Protocol='auto'|'tcp'|'udp'|'quic';
export type Connection='auto'|'direct'|'warpProxy'|'warpSystem';
export type SiteRule={id:string;name:string;domain:string;components:ComponentKind[];resourceDomains:string[];protocol:Protocol;ports:string;connection:Connection};
export const protocolOptions=[['auto','Авто'],['tcp','TCP'],['udp','UDP'],['quic','QUIC']] as const;
export const connectionOptions=[['auto','Авто'],['direct','Прямое'],['warpProxy','WARP-прокси'],['warpSystem','Системный WARP']] as const;
export const emptyRule=():SiteRule=>({id:crypto.randomUUID(),name:'',domain:'',components:['page'],resourceDomains:[],protocol:'auto',ports:'',connection:'auto'});
export function proxyBlocked(rule:Pick<SiteRule,'protocol'|'components'>){return rule.protocol==='udp'||rule.protocol==='quic'||rule.components.some(c=>c==='voice'||c==='game')}
export function normalizePorts(value:string){
  if(!value.trim())return {value:''};
  const items=value.split(',').map(s=>s.trim());const result:string[]=[];
  for(const item of items){if(!/^\d+(?:\s*-\s*\d+)?$/.test(item))return {value,error:'Порты: число или диапазон через запятую. Например: 80, 443, 10000-20000.'};const parts=item.split('-').map(Number);if(parts.some(n=>n<1||n>65535)||parts.length===2&&parts[0]>parts[1])return {value,error:'Порты должны быть от 1 до 65535, начало диапазона не больше конца.'};result.push(parts.join('-'))}
  return {value:[...new Set(result)].join(', ')};
}
export function ruleSummary(rule:SiteRule){return `${rule.domain} · ${rule.components.map(c=>componentOptions.find(o=>o[0]===c)?.[1]).join(', ')}`}
