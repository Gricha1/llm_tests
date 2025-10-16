import torch
import torch.nn.functional as F
from torch import nn


def clamped_entropy_from_logits(logits: torch.Tensor, clamp_p: float):
    """Calculate entropy from logits with token space clamping."""
    logits_cpu = logits.cpu().detach()
    with torch.no_grad():   
        k = int(logits_cpu.size(-1)*clamp_p)
        _, rm_indices = torch.topk(logits_cpu,k=k,dim=-1,largest=False)
        row_indices = torch.arange(logits_cpu.size(0)).unsqueeze(1)
        rm_mask = torch.zeros_like(logits_cpu,dtype=torch.bool)
        rm_mask[row_indices,rm_indices]=True
        del logits_cpu, row_indices, rm_indices
    clamped_logits = logits.masked_fill(rm_mask.to(logits.device), -torch.inf)
    del rm_mask
    torch.cuda.empty_cache()
    clamped_pd = torch.nn.functional.softmax(clamped_logits, dim=-1)
    clamped_entropy = torch.logsumexp(clamped_logits, dim=-1) - torch.sum(clamped_pd * logits, dim=-1)
    return clamped_entropy


def clamped_entropy_from_logits_with_chunking(logits: torch.Tensor, clamp_p: float, chunk_size:int=2048):
    """Calculate entropy from logits with token space clamping."""
    logits_cpu = logits.cpu().detach()
    with torch.no_grad():   
        k = int(logits_cpu.size(-1)*clamp_p)
        _, rm_indices = torch.topk(logits_cpu,k=k,dim=-1,largest=False)
        row_indices = torch.arange(logits_cpu.size(0)).unsqueeze(1)
        rm_mask = torch.zeros_like(logits_cpu,dtype=torch.bool)
        rm_mask[row_indices,rm_indices]=True
        del logits_cpu, row_indices, rm_indices
    clamped_logits = logits.masked_fill(rm_mask.to(logits.device), -torch.inf)
    del rm_mask
    torch.cuda.empty_cache()
    clamped_entropy = torch.zeros(logits.shape[0], device=logits.device)
    for i in range(0, logits.shape[0], chunk_size):
        logits_chunk = clamped_logits[i : i + chunk_size]
        pd_chunk = torch.nn.functional.softmax(logits_chunk, dim=-1)
        entropy_chunk = torch.logsumexp(logits_chunk, dim=-1) - torch.sum(pd_chunk * logits[i : i + chunk_size], dim=-1)
        clamped_entropy[i : i + chunk_size] = entropy_chunk
    return clamped_entropy
