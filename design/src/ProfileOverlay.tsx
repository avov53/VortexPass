import { useEffect, useRef, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { Icon } from './Icon';
function updateLayers(){
  const layers=[...document.querySelectorAll<HTMLElement>('.profile-overlay')];
  const shell=document.querySelector<HTMLElement>('.app-shell');if(shell)shell.inert=layers.length>0;
  layers.forEach((layer,i)=>{layer.inert=i<layers.length-1});
}
export default function ProfileOverlay({title,children,onClose,drawer=false}:{title:string;children:ReactNode;onClose:()=>void;drawer?:boolean}){
  const ref=useRef<HTMLDivElement>(null);
  useEffect(()=>{const previous=document.activeElement as HTMLElement;updateLayers();const target=ref.current?.querySelector<HTMLElement>('[data-autofocus]')??ref.current?.querySelector<HTMLElement>('input,textarea,button');target?.focus();return ()=>{queueMicrotask(()=>{updateLayers();if(previous?.isConnected&&!previous.closest('[inert]'))previous.focus()})}},[]);
  return createPortal(<div className={`profile-overlay ${drawer?'drawer-overlay':''}`}>
    <div className="overlay-backdrop" onClick={onClose}/>
    <div ref={ref} className={`profile-dialog ${drawer?'profile-drawer':''}`} role="dialog" aria-modal="true" aria-label={title} onKeyDown={e=>{
      if(e.key==='Escape'){e.stopPropagation();onClose()}
      if(e.key==='Tab'){const targets=[...ref.current!.querySelectorAll<HTMLElement>('button:not(:disabled),input:not(:disabled),textarea:not(:disabled),[tabindex="0"]')].filter(el=>el.getClientRects().length);const first=targets[0],last=targets.at(-1);if(e.shiftKey&&document.activeElement===first){e.preventDefault();last?.focus()}else if(!e.shiftKey&&document.activeElement===last){e.preventDefault();first?.focus()}}
    }}><header className="dialog-header"><h2>{title}</h2><button className="subtle-icon" aria-label={`Закрыть: ${title}`} onClick={onClose}><Icon name="close"/></button></header>{children}</div>
  </div>,document.body);
}
