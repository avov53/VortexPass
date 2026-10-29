import HalloweenScene from "./HalloweenScene";
import { useEffect, useRef, useState, type ReactNode } from "react";
import { Tooltip } from "@openai/apps-sdk-ui/components/Tooltip";
import { Icon, type IconName } from "./Icon";
import type { Tab } from "../NativeState";
import { postNativeMessage } from "../NativeBridge";
const pages: { id: Tab; name: string; icon: IconName }[] = [
  {id:"home",name:"Подключение",icon:"home"},{id:"profiles",name:"Профили",icon:"space"},
  {id:"warp",name:"Cloudflare WARP",icon:"globe"},{id:"activity",name:"Журнал",icon:"clock"},{id:"settings",name:"Настройки",icon:"settings"}
];
export function DesignShell({tab,onTab,theme,onTheme,reduceMotion,version,children}:{tab:Tab;onTab:(tab:Tab)=>void;theme:string;onTheme:(theme:string)=>void;reduceMotion:boolean;version:string;children:ReactNode}){
  const [season,setSeason]=useState(()=>{try{return localStorage.getItem("vortexpass.season")??"halloween"}catch{return "halloween"}});
  useEffect(()=>{document.documentElement.dataset.season=season;postNativeMessage("settings.seasonalIcon",season==="halloween")},[season]);
  useEffect(()=>{const update=()=>{try{setSeason(localStorage.getItem("vortexpass.season")??"halloween")}catch{}};window.addEventListener("vortexpass-season",update);return()=>window.removeEventListener("vortexpass-season",update)},[]);
  const [menu,setMenu]=useState(false);const scroll=useRef<HTMLElement>(null);
  useEffect(()=>{document.documentElement.dataset.theme=theme;document.documentElement.dataset.reducedMotion=String(reduceMotion)},[theme,reduceMotion]);
  useEffect(()=>{scroll.current?.scrollTo(0,0);setMenu(false)},[tab]);
  useEffect(()=>{const close=(e:KeyboardEvent)=>{if(e.key==="Escape")setMenu(false)};window.addEventListener("keydown",close);return()=>window.removeEventListener("keydown",close)},[]);
  return <div className={`app-shell ${tab==="settings"?"settings-mode":""}`}>
    <aside className="rail" aria-label="Разделы приложения"><div className="rail-top">{pages.map(page=><Tooltip key={page.id} content={page.name} openDelay={350} side="right" contentClassName="reference-tooltip"><button className={`rail-button ${tab===page.id?"active":""}`} aria-label={page.name} aria-current={tab===page.id?"page":undefined} onClick={()=>onTab(page.id)}><Icon name={page.icon} size={23}/></button></Tooltip>)}</div></aside>
    <section className="workspace"><header className="workspace-header"><Icon name={tab==="settings"?"settings":"folder"} size={19}/><strong>{pages.find(page=>page.id===tab)?.name}</strong><div className="header-actions"><button className="subtle-icon" aria-label="Дополнительные действия" aria-expanded={menu} onClick={()=>setMenu(v=>!v)}><Icon name="more"/></button></div>
      {menu&&<><button className="menu-scrim" aria-label="Закрыть меню" onClick={()=>setMenu(false)}/><div className="action-menu"><button onClick={()=>{onTheme(theme==="light"?"dark":"light");setMenu(false)}}><Icon name="sun"/>{theme==="light"?"Тёмная тема":"Светлая тема"}</button><button onClick={()=>onTab("settings")}><Icon name="settings"/>Настройки</button><button onClick={()=>postNativeMessage("window.quit")}><Icon name="close"/>Выйти из VortexPass</button></div></>}
    </header><main className="content-scroll" ref={scroll}><div className={`page-content ${tab==="settings"?"settings-content":""}`}>{season==="halloween"&&tab==="home"&&<HalloweenScene/>}{children}</div><footer>VortexPass {version} · Основные профили работают локально. WARP подключается отдельно.</footer></main></section>
  </div>;
}


