import { useEffect, useState, type CSSProperties } from 'react';
import { Icon } from './Icon';
const names=['Мягкий','Сбалансированный','Усиленный','Активный','Максимальный'];
const particles=Array.from({length:18},(_,i)=>({x:5+(i*37%86),y:18+(i*29%66),size:i%4===0?2.3:1.3,duration:2.8+(i%5)*.45,delay:-i*.43,dx:(i%2?1:-1)*(3+i%4),dy:(i%3?1:-1)*(2+i%3)}));
export default function LevelSlider({value,onChange}:{value:number;onChange:(value:number)=>void}){
  const [position,setPosition]=useState(value);
  const [dragging,setDragging]=useState(false);
  useEffect(()=>{if(!dragging)setPosition(value)},[value,dragging]);
  const tone=value===5?'max':value>=3?'medium':'light';
  const progress=(position-1)/4;
  const change=(next:number)=>{const clamped=Math.max(1,Math.min(5,next));setPosition(clamped);onChange(Math.round(clamped))};
  return <div className={`level-slider ${tone} ${dragging?'dragging':''}`} style={{'--position':(position-1)/4} as CSSProperties}>
    <div className="slider-heading"><Icon name="shield" size={16}/><strong>{names[value-1]}</strong><button type="button" aria-label="Сбросить интенсивность обхода" onClick={()=>change(2)}><Icon name="clock" size={17}/></button></div>
    <div className="slider-track-wrap"><div className="slider-track"><span className="slider-fill" style={{width:`calc(${progress*100}% + ${20-progress*40}px)`}}>{value===5&&<span className="slider-particles" aria-hidden="true">{particles.map((p,i)=><span key={i} className="slider-particle" style={{left:`${p.x}%`,top:`${p.y}%`,width:p.size,height:p.size,'--drift-time':`${p.duration}s`,'--sparkle-time':`${1.4+(i%4)*.6}s`,'--delay':`${p.delay}s`,'--dx':`${p.dx}px`,'--dy':`${p.dy}px`} as CSSProperties}/>)}</span>}</span></div><div className="slider-markers">{[0,1,2,3,4].map(n=><span key={n} className="slider-dot" style={{left:`${n*25}%`}}/>) }<span className="slider-thumb" style={{left:`${((position-1)/4)*100}%`}}/></div>
      <input type="range" min="1" max="5" step="0.01" value={position} aria-label="Интенсивность обхода" aria-valuetext={`${value} из 5: ${names[value-1]}`} aria-valuenow={value}
        onChange={e=>change(Number(e.target.value))} onPointerDown={e=>{e.currentTarget.setPointerCapture(e.pointerId);setDragging(true)}} onPointerUp={()=>setDragging(false)} onPointerCancel={()=>setDragging(false)} onBlur={()=>setDragging(false)}
        onKeyDown={e=>{const delta=['ArrowRight','ArrowUp','PageUp'].includes(e.key)?1:['ArrowLeft','ArrowDown','PageDown'].includes(e.key)?-1:0;if(delta||e.key==='Home'||e.key==='End'){e.preventDefault();change(e.key==='Home'?1:e.key==='End'?5:value+delta)}}}/>
    </div>
  </div>;
}
