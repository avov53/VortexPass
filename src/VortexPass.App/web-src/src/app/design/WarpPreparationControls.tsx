import { useState } from "react";
import { Button } from "@openai/apps-sdk-ui/components/Button";
import { Checkbox } from "@openai/apps-sdk-ui/components/Checkbox";
import { postNativeMessage, postNativePayload } from "../NativeBridge";
import type { NativeState } from "../NativeState";

export function WarpPreparationControls({ app }: { app: NativeState }) {
 const [accepted, setAccepted] = useState(false);
 const preparing = !["idle", "ready", "failed"].includes(app.warpPreparation.state);
 const busy = app.controlsBusy || app.warp.pending;
 return <div className="warp-preparation-controls">
  <Checkbox checked={accepted} onCheckedChange={setAccepted} disabled={preparing}
   label="Принимаю условия Cloudflare для установки WARP и регистрации устройства без аккаунта"/>
  <a className="warp-terms-link" href="https://www.cloudflare.com/application/terms/" target="_blank" rel="noreferrer">Открыть условия Cloudflare ↗</a>
  <div className="warp-preparation-actions">
   <Button color="primary" disabled={!accepted || busy} onClick={() => postNativePayload({type:"warp.prepare", acceptTerms:true})}>Подготовить WARP</Button>
   {preparing && <Button color="secondary" onClick={() => postNativeMessage("warp.prepare.cancel")}>Отменить подготовку</Button>}
  </div>
  <p className="rule-note" role="status">{preparing ? app.warpPreparation.detail || "Подготовка WARP…" : busy ? "Дождитесь завершения текущей сетевой операции." : !accepted ? "Чтобы начать подготовку, отметьте принятие условий Cloudflare." : app.warpPreparation.detail || "Можно начать подготовку WARP."}</p>
 </div>;
}
