import type { IconName } from './Icon';
import { componentOptions,connectionOptions,normalizePorts,protocolOptions,proxyBlocked,type SiteRule } from './siteRules';
export const PROFILE_STORAGE_KEY='vortexpass.halloween.profiles.v2';
export const LEGACY_PROFILE_STORAGE_KEY='vortexpass.halloween.profiles.v1';
export const MAX_PROFILE_BYTES=64*1024;
export const serviceOptions:{id:string;name:string;description:string;icon:IconName}[]=[
  {id:'youtube',name:'YouTube',description:'Видео и трансляции',icon:'video'},
  {id:'discord',name:'Discord',description:'Общение и голосовая связь',icon:'message'},
  {id:'telegram',name:'Telegram',description:'Telegram Desktop и Web',icon:'send'},
  {id:'web',name:'Web',description:'Повседневный интернет',icon:'globe'},
  {id:'games',name:'Game Filter',description:'Отдельные игровые соединения',icon:'game'}
];
export const gameOptions=['Roblox','Phasmophobia','Dead by Daylight'];
export const levelNames=['Мягкий','Сбалансированный','Усиленный','Активный','Максимальный'];
export function countLabel(count:number,forms:[string,string,string]){const n=count%100;return `${count} ${forms[n>=11&&n<=14?2:count%10===1?0:count%10>=2&&count%10<=4?1:2]}`}
export type UserProfile={id:string;version:2;name:string;services:string[];games:string[];domains:string[];siteRules:SiteRule[];level:number;updatedAt:string};
export type ProfileDraft={name:string;services:string[];games:string[];domainsText:string;siteRules:SiteRule[];level:number};
export const emptyDraft=():ProfileDraft=>({name:'',services:[],games:[],domainsText:'',siteRules:[],level:2});
export const toDraft=(p:UserProfile):ProfileDraft=>({name:p.name,services:[...p.services],games:[...p.games],domainsText:p.domains.join('\n'),siteRules:structuredClone(p.siteRules),level:p.level});
export function normalizeDomains(text:string):{domains:string[];error?:string}{
  const domains:string[]=[];
  for(const entry of text.split(/\r?\n/)){
    const raw=entry.trim().toLowerCase();if(!raw)continue;
    const wildcard=raw.startsWith('*.');const host=wildcard?raw.slice(2):raw;
    try{
      if(/[\s/:?#@\\]/.test(host))throw new Error();
      const ascii=new URL(`https://${host}`).hostname;
      const parts=ascii.split('.');
      if(ascii.length>253||parts.length<2||parts.some(part=>!part||part.length>63||!/^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$/.test(part))||/^\d+$/.test(parts.at(-1)!))throw new Error();
      const domain=(wildcard?'*.':'')+ascii;if(!domains.includes(domain))domains.push(domain);
    }catch{return {domains,error:`Некорректный домен: ${entry.trim()}. Пример: example.com или *.example.com`}}
  }
  if(domains.length>500)return {domains,error:'Допускается не больше 500 доменов.'};
  return {domains};
}
export function siteRuleErrors(rule:SiteRule){
  const domain=normalizeDomains(rule.domain);const resources=normalizeDomains(rule.resourceDomains.join('\n'));
  return {name:!rule.name.trim()||rule.name.trim().length>80?'Введите название правила (до 80 символов).':undefined,domain:domain.error??(domain.domains.length!==1?'Введите один основной домен.':undefined),resources:resources.error,components:!rule.components.length?'Выберите хотя бы один компонент сайта.':undefined,ports:normalizePorts(rule.ports).error,connection:rule.connection==='warpProxy'&&proxyBlocked(rule)?'WARP-прокси не поддерживает этот тип трафика.':undefined};
}
function normalizeRule(rule:SiteRule):SiteRule{return {...rule,name:rule.name.trim(),domain:normalizeDomains(rule.domain).domains[0],components:[...new Set(rule.components)],resourceDomains:normalizeDomains(rule.resourceDomains.join('\n')).domains,ports:normalizePorts(rule.ports).value}}
export function draftErrors(draft:ProfileDraft){
  const domainResult=normalizeDomains(draft.domainsText);
  const encodedSize=new TextEncoder().encode(serializeProfile({id:'00000000-0000-0000-0000-000000000000',version:2,name:draft.name.trim(),services:draft.services,games:draft.games,domains:domainResult.domains,siteRules:draft.siteRules,level:draft.level,updatedAt:'2000-01-01T00:00:00.000Z'})).byteLength;
  return {name:!draft.name.trim()?'Введите название профиля.':draft.name.trim().length>80?'Название не длиннее 80 символов.':undefined,domains:domainResult.error,rules:draft.siteRules.some(r=>Object.values(siteRuleErrors(r)).some(Boolean))?'Проверьте поля правил сайтов.':undefined,content:!draft.services.length&&!draft.games.length&&!domainResult.domains.length&&!draft.siteRules.length?'Выберите сервис, игру, добавьте домен или правило сайта.':encodedSize>MAX_PROFILE_BYTES?'Профиль превышает 64 КБ. Уменьшите список доменов и правил.':undefined};
}
export function createProfile(draft:ProfileDraft,id:string=crypto.randomUUID()):UserProfile{
  const errors=draftErrors(draft);if(Object.values(errors).some(Boolean))throw new Error('Проверьте поля профиля.');
  return {id,version:2,name:draft.name.trim(),services:[...draft.services],games:[...draft.games],domains:normalizeDomains(draft.domainsText).domains,siteRules:draft.siteRules.map(normalizeRule),level:draft.level,updatedAt:new Date().toISOString()};
}
export function summary(profile:UserProfile){
  return [...profile.services.map(id=>serviceOptions.find(s=>s.id===id)?.name??id),...profile.games,profile.domains.length?countLabel(profile.domains.length,['домен','домена','доменов']):null,profile.siteRules.length?countLabel(profile.siteRules.length,['правило сайта','правила сайтов','правил сайтов']):null].filter(Boolean).join(' · ');
}
function strings(value:unknown,allowed?:string[]):string[]{
  if(!Array.isArray(value)||value.some(s=>typeof s!=='string'||(allowed&&!allowed.includes(s))))throw new Error('Неверный список сервисов, игр или доменов.');
  return [...new Set(value)];
}
export function validateProfile(value:unknown):UserProfile{
  if(!value||typeof value!=='object'||Array.isArray(value))throw new Error('Неверный формат профиля.');
  const p=value as Record<string,unknown>;
  if(p.version!==1&&p.version!==2)throw new Error('Версия профиля не поддерживается.');
  if(typeof p.id!=='string'||!p.id||p.id.length>128||typeof p.name!=='string'||typeof p.updatedAt!=='string'||!Number.isFinite(Date.parse(p.updatedAt))||!Number.isInteger(p.level)||(p.level as number)<1||(p.level as number)>5)throw new Error('Профиль содержит некорректные поля.');
  const services=strings(p.services,serviceOptions.map(s=>s.id));const games=strings(p.games,gameOptions);const domains=strings(p.domains);
  const siteRules:SiteRule[]=[];
  if(p.version===2){if(!Array.isArray(p.siteRules))throw new Error('Неверный список правил сайтов.');for(const value of p.siteRules){if(!value||typeof value!=='object')throw new Error('Неверное правило сайта.');const r=value as Record<string,unknown>;if(typeof r.id!=='string'||!r.id||r.id.length>128||siteRules.some(x=>x.id===r.id)||typeof r.name!=='string'||typeof r.domain!=='string'||typeof r.ports!=='string'||!protocolOptions.some(o=>o[0]===r.protocol)||!connectionOptions.some(o=>o[0]===r.connection))throw new Error('Правило сайта содержит некорректные поля.');const rule={id:r.id,name:r.name,domain:r.domain,ports:r.ports,components:strings(r.components,componentOptions.map(o=>o[0])) as SiteRule['components'],resourceDomains:strings(r.resourceDomains),protocol:r.protocol as SiteRule['protocol'],connection:r.connection as SiteRule['connection']};if(Object.values(siteRuleErrors(rule)).some(Boolean))throw new Error('Правило сайта содержит некорректные настройки.');siteRules.push(normalizeRule(rule))}}
  const draft={name:p.name,services,games,domainsText:domains.join('\n'),siteRules,level:p.level as number};
  const errors=draftErrors(draft);if(Object.values(errors).some(Boolean))throw new Error(Object.values(errors).find(Boolean));
  return {...createProfile(draft,p.id),updatedAt:p.updatedAt};
}
export function serializeProfile(profile:UserProfile){return JSON.stringify({format:'vortexpass-profile',...profile},null,2)}
export function parseProfileFile(text:string):UserProfile{
  if(new TextEncoder().encode(text).byteLength>MAX_PROFILE_BYTES)throw new Error('Размер профиля превышает 64 КБ.');
  let value;try{value=JSON.parse(text)}catch{throw new Error('Не удалось прочитать JSON-профиль.')}
  if(value?.format!=='vortexpass-profile')throw new Error('Это не файл профиля VortexPass.');
  return validateProfile(value);
}
export function profileLink(profile:UserProfile){
  const bytes=new TextEncoder().encode(serializeProfile(profile));
  return 'vortexpass-profile:'+btoa(Array.from(bytes,b=>String.fromCharCode(b)).join('')).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
}
export function parseProfileLink(link:string){
  const value=link.trim();if(!value.startsWith('vortexpass-profile:'))throw new Error('Вставьте код-ссылку vortexpass-profile:.');
  const data=value.slice('vortexpass-profile:'.length);
  if(!data||data.length>Math.ceil(MAX_PROFILE_BYTES*4/3)||!/^[a-zA-Z0-9_-]+$/.test(data))throw new Error('Код-ссылка повреждена или превышает 64 КБ.');
  try{return parseProfileFile(new TextDecoder('utf-8',{fatal:true}).decode(Uint8Array.from(atob(data.replace(/-/g,'+').replace(/_/g,'/')),c=>c.charCodeAt(0))))}catch(e){throw new Error(e instanceof Error?e.message:'Не удалось прочитать код-ссылку.')}
}
export function importCopy(profile:UserProfile,existing:UserProfile[]):UserProfile{
  let name=profile.name;let number=1;while(existing.some(p=>p.name.toLowerCase()===name.toLowerCase())){name=`${profile.name.slice(0,60)} (копия${number===1?'':` ${number}`})`;number++}
  return {...profile,id:crypto.randomUUID(),name,updatedAt:new Date().toISOString()};
}
export function loadProfiles():{profiles:UserProfile[];warning:string}{
  try{
    const raw=localStorage.getItem(PROFILE_STORAGE_KEY)??localStorage.getItem(LEGACY_PROFILE_STORAGE_KEY);if(!raw)return {profiles:[],warning:''};
    const stored=JSON.parse(raw);if(![1,2].includes(stored.version)||!Array.isArray(stored.profiles))throw new Error();
    const profiles:UserProfile[]=[];let bad=false;
    for(const entry of stored.profiles){try{const p=validateProfile(entry);if(profiles.some(x=>x.id===p.id))throw new Error();profiles.push(p)}catch{bad=true}}
    return {profiles,warning:bad?'Некоторые сохранённые профили повреждены и не показаны. Исходные данные будут заменены только после сохранения ваших изменений.':''};
  }catch{return {profiles:[],warning:'Не удалось загрузить локальные профили. Экспортируйте важные профили в файл; новые изменения попробуем сохранить при первом сохранении.'}}
}
