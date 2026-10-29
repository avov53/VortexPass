import "./network-recovery.css";
import { WarpPreparationControls } from "./design/WarpPreparationControls";
import { useEffect, useState } from "react";
import { Button } from "@openai/apps-sdk-ui/components/Button";
import { DesignShell } from "./design/DesignShell";
import { NativeProfiles } from "./design/NativeProfiles";
import { NativeWarp } from "./design/NativeWarp";
import { NativeSettings, NativeJournal } from "./design/NativeSettings";
import ProfileOverlay from "./design/ProfileOverlay";
import { useNativeApp } from "./NativeState";
import { nativeBridge, postNativeMessage, postNativePayload } from "./NativeBridge";
import "./design/design.css";
import "./design/halloween.css";
export default function App(){
 const app=useNativeApp();const [notice,setNotice]=useState("");const [updateDismissed,setUpdateDismissed]=useState("");

 const showWarpSetup=!app.warp.setupChecked||!(app.warp.installed&&app.warp.registered);
 useEffect(()=>{const log=app.logs.at(-1);if(log?.level==="error")setNotice(log.msg);else if(app.connections.change==="saved")setNotice("Профиль сохранён");},[app.logs,app.connections]);
 useEffect(()=>{if(!notice)return;const timer=setTimeout(()=>setNotice(""),6000);return()=>clearTimeout(timer)},[notice]);
 const version=app.update.currentVersion??"—";
 const theme=(value:string)=>postNativePayload({type:"settings.appearance",theme:value,reduceMotion:app.settings.reduceMotion??false});
 return <>
  <DesignShell tab={app.tab} onTab={app.setTab} theme={app.settings.theme??"dark"} onTheme={theme} reduceMotion={!!app.settings.reduceMotion} version={version}>
    {!nativeBridge()&&<div className="storage-warning" role="status">Предпросмотр оформления. Сетевые команды доступны только внутри приложения.</div>}
    {(app.tab==="home"||app.tab==="profiles")&&<NativeProfiles app={app}/>}
    {app.tab==="warp"&&<NativeWarp app={app}/>}
    {app.tab==="activity"&&<NativeJournal app={app}/>}
    {app.tab==="settings"&&<NativeSettings app={app}/>}
  </DesignShell>
  <div className={`notice ${notice?"visible":""}`} role="status">{notice}</div>
  {showWarpSetup&&<ProfileOverlay title="Подготовьте Cloudflare WARP" dismissible={false} onClose={()=>{}}><div className="dialog-body warp-onboarding-body"><p>Для Telegram Web нужен официальный клиент WARP. Официальный установщик входит в комплект VortexPass. Программа проверит его подпись, установит клиент и зарегистрирует устройство без пользовательского аккаунта.</p><p className="rule-note">Для установки из комплекта скачивание не требуется. Для регистрации включается временный обход Cloudflare; при отказе проверяются до трёх вариантов HTTPS. Полный туннель и системный прокси на этом этапе не включаются. После успешной подготовки это предложение больше не появится.</p><p>Для продолжения работы требуется подготовить WARP. Условия принимаете только вы; без согласия можно выйти из приложения.</p>{app.warp.setupChecked?<WarpPreparationControls app={app}/>:<p role="status">Проверяем установленный WARP…</p>}</div><div className="dialog-footer"><Button color="secondary" variant="ghost" onClick={()=>postNativeMessage("window.quit")}>Выйти из приложения</Button></div></ProfileOverlay>}
    {((app.update.state==="availablePrompt"&&updateDismissed!==app.update.availableVersion)||["downloading","preparing","installing"].includes(app.update.state))&&<ProfileOverlay title={["downloading","preparing","installing"].includes(app.update.state)?"Обновляем VortexPass":"Доступно обновление"} onClose={()=>{if(!["downloading","preparing","installing"].includes(app.update.state))setUpdateDismissed(app.update.availableVersion??"")}}><div className="dialog-body update-body"><div className="update-brand"><img className="update-logo-standard" src="vortexpass-logo.png" alt=""/><img className="update-logo-season" src="halloween-logo.png" alt=""/><div><span className="update-eyebrow">VORTEXPASS</span><h3>Свежая версия. На связи.</h3></div></div><p className="update-version">{app.update.currentVersion??"Текущая версия"} → {app.update.availableVersion??"Новая версия"}</p><p>{app.update.state==="downloading"?"Скачиваем обновление…":app.update.state==="preparing"?"Проверяем комплект и подготавливаем установку…":app.update.state==="installing"?"Завершаем приложение для установки…":"Можно установить сейчас или продолжить работу."}</p>{["downloading","preparing","installing"].includes(app.update.state)&&<div className="update-indicator" role="status" aria-label="Выполняется обновление"><span/></div>}</div>{!["downloading","preparing","installing"].includes(app.update.state)&&<div className="dialog-footer"><Button color="secondary" variant="ghost" onClick={()=>setUpdateDismissed(app.update.availableVersion??"")}>Позже</Button><Button color="primary" onClick={()=>postNativeMessage("update.installNow")}>Обновить сейчас</Button></div>}</ProfileOverlay>}
 </>;
}



