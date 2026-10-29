import { useEffect, useRef } from "react";
type Bridge = { postMessage: (message: unknown) => void; addEventListener: (type: string, listener: (event: any) => void) => void; removeEventListener: (type: string, listener: (event: any) => void) => void };
export function nativeBridge(): Bridge | undefined { return (window as Window & { chrome?: { webview?: Bridge } }).chrome?.webview; }
export function postNativePayload(payload: Record<string, unknown>) { nativeBridge()?.postMessage(payload); }
export function postNativeMessage(type: string, value?: boolean, serviceId?: string) { postNativePayload({ type, value, serviceId }); }
export function useNativeMessage(handler: (message: any) => void) {
  const current = useRef(handler); current.current = handler;
  useEffect(() => {
    const bridge = nativeBridge(); if (!bridge?.addEventListener) return;
    const listener = (event: any) => current.current(event.data);
    bridge.addEventListener("message", listener);
    return () => bridge.removeEventListener("message", listener);
  }, []);
}
