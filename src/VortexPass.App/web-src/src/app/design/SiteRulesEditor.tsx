import { Button } from "@openai/apps-sdk-ui/components/Button";
import DesignSelect from "./DesignSelect";
export type SiteRule={id:string;name:string;domain:string;components:string[];resourceDomains:string[];protocol:"auto"|"tcp"|"udp"|"quic";ports:string;connection:"auto"|"direct"|"warpProxy"|"warpSystem"};
const components=[["page","Страница"],["images","Изображения"],["video","Видео"],["voice","Голосовая связь"],["game","Игровой трафик"]];
export function ruleError(rule:SiteRule){
 if(!rule.name.trim()||!rule.domain.trim()||!rule.components.length)return "Заполните название, домен и компоненты правила.";
 if(rule.protocol==="udp")return "Произвольный UDP по домену не поддерживается.";
 if(rule.connection==="warpProxy"&&(rule.protocol==="quic"||rule.components.some(c=>c==="voice"||c==="game")))return "WARP-прокси обслуживает только Web/TCP.";
 if(rule.connection==="warpProxy"&&rule.ports.trim())return "Для WARP-прокси оставьте порты в режиме Авто.";
 return "";
}
export default function SiteRulesEditor({rules,onChange}:{rules:SiteRule[];onChange:(rules:SiteRule[])=>void}){
 const update=(id:string,patch:Partial<SiteRule>)=>onChange(rules.map(r=>r.id===id?{...r,...patch}:r));
 return <details className="site-rules-section"><summary>Правила сайтов <span className="demo-badge">{rules.length}</span></summary><div className="site-rules-body"><p className="rule-note">Правила применяются к указанным доменам. Категории описывают назначение: домены ресурсов и CDN вводятся вручную.</p>
 {rules.map((r,index)=><section key={r.id} className="site-rule-card"><div className="rule-card-heading"><h4>Правило {index+1}</h4><Button type="button" color="danger" variant="ghost" size="sm" onClick={()=>onChange(rules.filter(v=>v.id!==r.id))}>Удалить правило</Button></div>
 <label className="field-label" htmlFor={`rule-name-${r.id}`}>Название правила</label><input className="profile-input" id={`rule-name-${r.id}`} maxLength={80} value={r.name} placeholder="Например, изображения на сайте" onChange={e=>update(r.id,{name:e.target.value})}/>
 <label className="field-label" htmlFor={`rule-domain-${r.id}`}>Основной домен</label><input className="profile-input" id={`rule-domain-${r.id}`} value={r.domain} placeholder="example.com или *.example.com" onChange={e=>update(r.id,{domain:e.target.value})}/>
 <div className="field-label">Компоненты сайта</div><div className="game-choices">{components.map(([id,name])=><label key={id}><input type="checkbox" checked={r.components.includes(id)} onChange={e=>update(r.id,{components:e.target.checked?[...r.components,id]:r.components.filter(v=>v!==id)})}/>{name}</label>)}</div>
 <label className="field-label" htmlFor={`rule-resources-${r.id}`}>Домены ресурсов / CDN</label><textarea className="profile-input" id={`rule-resources-${r.id}`} rows={3} value={r.resourceDomains.join("\n")} placeholder={"images.example.com\n*.cdn.example.com"} onChange={e=>update(r.id,{resourceDomains:e.target.value.split("\n")})}/>
 <details className="rule-advanced"><summary>Дополнительно</summary><label className="field-label">Протокол<DesignSelect className="profile-input" value={r.protocol} onChange={e=>update(r.id,{protocol:e.target.value as SiteRule["protocol"]})}><option value="auto">Авто</option><option value="tcp">TCP</option><option value="udp" disabled>UDP</option><option value="quic">QUIC</option></DesignSelect></label>
 <label className="field-label" htmlFor={`rule-ports-${r.id}`}>Порты</label><input className="profile-input" id={`rule-ports-${r.id}`} value={r.ports} placeholder="Авто, например 443, 8443" onChange={e=>update(r.id,{ports:e.target.value})}/>
 <label className="field-label">Способ подключения<DesignSelect className="profile-input" value={r.connection} onChange={e=>update(r.id,{connection:e.target.value as SiteRule["connection"]})}><option value="auto">Авто</option><option value="direct">Прямое</option><option value="warpProxy" disabled={r.protocol==="quic"||r.components.some(c=>c==="voice"||c==="game")}>WARP-прокси</option><option value="warpSystem">Системный WARP</option></DesignSelect></label><p className="rule-note">Произвольный голосовой/игровой UDP по домену не перехватывается.</p>{r.connection==="warpSystem"&&<p className="rule-note">Системный WARP действует на весь компьютер. Прямые правила не являются исключениями из туннеля.</p>}</details>
 {ruleError(r)&&<p className="field-error">{ruleError(r)}</p>}</section>)}
 <Button type="button" color="secondary" variant="soft" disabled={rules.length>=50} onClick={()=>onChange([...rules,{id:crypto.randomUUID(),name:"",domain:"",components:["page"],resourceDomains:[],protocol:"auto",ports:"",connection:"auto"}])}>Добавить правило</Button></div></details>;
}
