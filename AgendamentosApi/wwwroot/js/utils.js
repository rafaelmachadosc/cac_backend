// Utilidades compartilhadas (CPF/Telefone)
function digitsOnly(s){ return (s||'').replace(/\D+/g,''); }
function onlyDigits(s){ return digitsOnly(s); }

function enforce11(el){
  const v = digitsOnly(el.value).slice(0,11);
  if(el.value !== v) el.value = v;
}

function preventNonDigits(e){
  const ok = ['Backspace','Delete','ArrowLeft','ArrowRight','Tab','Home','End'];
  if (ok.includes(e.key)) return;
  if (!/^\d$/.test(e.key)) e.preventDefault();
}

